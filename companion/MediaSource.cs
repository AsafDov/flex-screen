using Windows.Media;
using Windows.Media.Control;

namespace FlexScreen.Companion;

/// <param name="Position">Seconds into the track at the moment the state was built.</param>
/// <param name="Shuffle">null when the app doesn't support shuffle.</param>
/// <param name="Repeat">"none", "one" or "all"; null when the app doesn't support repeat.</param>
/// <param name="ArtId">The <see cref="Art"/> for this track, or "" when there's none.</param>
record MediaState(bool Present, string Title, string Artist, string App, bool Playing,
                  double Position, double Length, bool? Shuffle, string? Repeat, string ArtId)
{
    public static readonly MediaState None = new(false, "", "", "", false, 0, 0, null, null, "");
}

/// <summary>
/// Now playing and transport controls from Windows' media sessions, the same source as the
/// media flyout: Spotify, browsers, and anything else that registers a session.
/// </summary>
/// <remarks>
/// Windows' own "current session" is whichever app changed state last, so with the Spotify
/// app and a browser both open it flips between them. The session shown is chosen here
/// instead (<see cref="Choose"/>).
/// </remarks>
sealed class MediaSource
{
    /// <summary>Square album-art size sent to the deck (the Audio page shows it at this size).</summary>
    const int ArtSize = 180;

    static readonly string[] Browsers = ["chrome", "msedge", "firefox", "opera", "brave", "vivaldi"];

    readonly object _gate = new();
    readonly SemaphoreSlim _refreshing = new(1, 1);
    readonly List<GlobalSystemMediaTransportControlsSession> _watched = new();
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    string _artTrack = "";

    public MediaState Current { get; private set; } = MediaState.None;
    public Art? CurrentArt { get; private set; }

    /// <summary>Raised on a thread-pool thread whenever <see cref="Current"/> changes.</summary>
    public event Action<MediaState>? Changed;

    /// <summary>Raised before the <see cref="Changed"/> that first refers to the new art.</summary>
    public event Action<Art>? ArtReady;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += (_, _) => Choose();
        _manager.CurrentSessionChanged += (_, _) => Choose();
        Choose();
    }

    public Task ToggleAsync() => Control(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task NextAsync() => Control(s => s.TrySkipNextAsync().AsTask());
    public Task PreviousAsync() => Control(s => s.TrySkipPreviousAsync().AsTask());

    public Task ToggleShuffleAsync() =>
        Control(s => s.TryChangeShuffleActiveAsync(!(s.GetPlaybackInfo()?.IsShuffleActive ?? false)).AsTask());

    /// <summary>Off → all → one → off, like Spotify's button.</summary>
    public Task CycleRepeatAsync() => Control(s =>
    {
        var next = s.GetPlaybackInfo()?.AutoRepeatMode switch
        {
            MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
            MediaPlaybackAutoRepeatMode.Track => MediaPlaybackAutoRepeatMode.None,
            _ => MediaPlaybackAutoRepeatMode.List,
        };
        return s.TryChangeAutoRepeatModeAsync(next).AsTask();
    });

    async Task Control(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        var session = _session;
        if (session == null) return;
        try
        {
            if (!await action(session)) Log.Write("media: the app refused the command");
        }
        catch (Exception e)
        {
            Log.Write($"media: {e.Message}");
        }
    }

    /// <summary>
    /// Picks the session to show: one that's playing, preferring a media app (the Spotify
    /// app) over a browser; with nothing playing, the one shown last, so pausing doesn't
    /// hand the deck to whichever app Windows touched most recently.
    /// </summary>
    void Choose()
    {
        var manager = _manager;
        if (manager == null) return;
        try
        {
            lock (_gate)
            {
                var sessions = manager.GetSessions().ToList();
                WatchPlayback(sessions);

                var playing = sessions.Where(IsPlaying).ToList();
                var shownApp = _session?.SourceAppUserModelId;
                var best = playing.Count > 0
                    ? playing.OrderBy(s => IsBrowser(s) ? 1 : 0)
                             .ThenBy(s => s.SourceAppUserModelId == shownApp ? 0 : 1)
                             .First()
                    : sessions.FirstOrDefault(s => s.SourceAppUserModelId == shownApp)
                      ?? manager.GetCurrentSession()
                      ?? sessions.FirstOrDefault();
                if (ReferenceEquals(best, _session)) return;
                Attach(best);
            }
        }
        catch (Exception e)
        {
            Log.Write($"media: choosing a session failed: {e.Message}");
        }
    }

    /// <summary>Listens for play/pause on every session, so one starting can take over.</summary>
    void WatchPlayback(List<GlobalSystemMediaTransportControlsSession> sessions)
    {
        foreach (var s in _watched) s.PlaybackInfoChanged -= OnAnyPlayback;
        _watched.Clear();
        foreach (var s in sessions)
        {
            s.PlaybackInfoChanged += OnAnyPlayback;
            _watched.Add(s);
        }
    }

    void OnAnyPlayback(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => Choose();

    void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaProperties;
            _session.PlaybackInfoChanged -= OnSessionChanged;
            _session.TimelinePropertiesChanged -= OnSessionChanged;
        }
        _session = session;
        if (session != null)
        {
            session.MediaPropertiesChanged += OnMediaProperties;
            session.PlaybackInfoChanged += OnSessionChanged;
            session.TimelinePropertiesChanged += OnSessionChanged;
            Log.Write($"media: following {AppName(session.SourceAppUserModelId)}");
        }
        _ = RefreshAsync(reloadArt: true);
    }

    // Track details changed: the art may have too, even with the same title (Chrome first
    // reports its own icon, then the page's artwork).
    void OnMediaProperties(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        _ = RefreshAsync(reloadArt: true);

    void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshAsync(reloadArt: false);

    async Task RefreshAsync(bool reloadArt)
    {
        // Events arrive in bursts; one refresh at a time keeps art and state in order.
        await _refreshing.WaitAsync();
        try
        {
            var session = _session;
            if (session == null)
            {
                Publish(MediaState.None);
                return;
            }
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            if (props == null || playback == null || timeline == null) return;  // session closing
            var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var track = $"{props.Title}\n{props.Artist}";
            if (reloadArt || track != _artTrack)
            {
                _artTrack = track;
                var art = await AlbumArt.LoadAsync(props.Thumbnail, ArtSize);
                if (art?.Id != CurrentArt?.Id)
                {
                    CurrentArt = art;
                    if (art != null) ArtReady?.Invoke(art);
                }
            }

            var length = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
            var position = (timeline.Position - timeline.StartTime).TotalSeconds;
            // Apps report the position only now and then; bring it up to now.
            if (playing) position += (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds;
            position = Math.Max(0, length > 0 ? Math.Min(position, length) : position);

            var controls = playback.Controls;
            bool? shuffle = controls?.IsShuffleEnabled == true ? playback.IsShuffleActive ?? false : null;
            string? repeat = controls?.IsRepeatEnabled == true
                ? playback.AutoRepeatMode switch
                {
                    MediaPlaybackAutoRepeatMode.Track => "one",
                    MediaPlaybackAutoRepeatMode.List => "all",
                    _ => "none",
                }
                : null;

            Publish(new MediaState(true, props.Title ?? "", props.Artist ?? "",
                                   AppName(session.SourceAppUserModelId), playing,
                                   Math.Round(position, 1), Math.Round(length, 1), shuffle, repeat,
                                   CurrentArt?.Id ?? ""));
        }
        catch (Exception e)
        {
            // Sessions vanish mid-call when the app closes; the next change event recovers.
            Log.Write($"media: refresh failed: {e.Message}");
        }
        finally
        {
            _refreshing.Release();
        }
    }

    void Publish(MediaState state)
    {
        lock (_gate)
        {
            if (state == Current) return;
            Current = state;
        }
        Changed?.Invoke(state);
    }

    static bool IsPlaying(GlobalSystemMediaTransportControlsSession s) =>
        s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    static bool IsBrowser(GlobalSystemMediaTransportControlsSession s) =>
        Browsers.Any(b => s.SourceAppUserModelId.Contains(b, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Spotify.exe" or "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" → "Spotify".</summary>
    static string AppName(string appId)
    {
        var name = appId;
        var bang = name.LastIndexOf('!');
        if (bang >= 0) name = name[(bang + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant() switch
        {
            "" => "",
            "chrome" => "Chrome",
            "msedge" => "Edge",
            "firefox" => "Firefox",
            _ => char.ToUpperInvariant(name[0]) + name[1..],
        };
    }
}
