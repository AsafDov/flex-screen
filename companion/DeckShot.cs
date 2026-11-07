using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace FlexScreen.Companion;

/// <summary>
/// A picture of the deck's screen, for checking layouts on the real board from this PC: the
/// deck answers "shot" with its frame buffer in base64 parts (RGB565), turned into a PNG here.
/// </summary>
sealed class DeckShot
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    readonly object _gate = new();
    TaskCompletionSource<byte[]?>? _pending;
    byte[] _pixels = [];
    bool[] _got = [];
    int _width, _height;

    /// <summary>Asks for a shot (with <paramref name="request"/>) and waits for it; null if it doesn't all arrive.</summary>
    public async Task<byte[]?> TakeAsync(Action request)
    {
        var pending = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending?.TrySetResult(null);
            _pending = pending;
            _got = [];
        }
        request();
        var done = await Task.WhenAny(pending.Task, Task.Delay(Timeout));
        if (done == pending.Task) return pending.Task.Result;
        lock (_gate)
        {
            var missing = _got.Count(g => !g);
            Log.Write($"shot: timed out, {(missing > 0 ? $"{missing} of {_got.Length} parts missing" : "nothing arrived")}");
            if (_pending == pending) _pending = null;
        }
        return null;
    }

    /// <summary>A "shot" part from the deck.</summary>
    public void Add(JsonNode m)
    {
        lock (_gate)
        {
            if (_pending == null) return;
            var (i, n, w, h) = ((int?)m["i"] ?? -1, (int?)m["n"] ?? 0, (int?)m["w"] ?? 0, (int?)m["h"] ?? 0);
            if (i < 0 || i >= n || w <= 0 || h <= 0) return;
            if (_got.Length != n || _width != w || _height != h)
            {
                (_width, _height, _got, _pixels) = (w, h, new bool[n], new byte[w * h * 2]);
            }
            byte[] part;
            try
            {
                part = Convert.FromBase64String((string?)m["d"] ?? "");
            }
            catch (FormatException)
            {
                return;  // a damaged line: the part stays missing
            }
            var at = i * 3072;
            if (at + part.Length > _pixels.Length) return;
            part.CopyTo(_pixels, at);
            _got[i] = true;
            if (_got.All(g => g))
            {
                _pending.TrySetResult(Png(_pixels, _width, _height));
                _pending = null;
            }
        }
    }

    static byte[] Png(byte[] rgb565, int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format16bppRgb565);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format16bppRgb565);
        try
        {
            for (var y = 0; y < height; y++) Marshal.Copy(rgb565, y * width * 2, data.Scan0 + y * data.Stride, width * 2);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return png.ToArray();
    }
}
