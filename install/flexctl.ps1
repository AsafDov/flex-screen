# flexctl: a note, a question or a progress bar on the flex-screen deck, from any script.
# The same three things Claude Code gets from the deck's tools, through the companion on
# this PC (http://localhost:48151/api/...). install.ps1 puts flexctl.cmd on the PATH.
#
#   flexctl notify "Backup done" [-level info|done|warning|error] [-from Backup]
#   flexctl ask "Deploy to production?" Yes No [-wait 15] [-from Deploy]
#   flexctl progress 3 12 "Uploading" [-from Backup]
#   flexctl progress -clear
#
# notify leaves a note that stays until OK is tapped. ask waits for a tap (up to -wait
# minutes, 15 unless given) and prints the option tapped. progress shows "Uploading 3/12" in
# the deck's status line until done reaches total, -clear, or 30 minutes without a word.
# Options take one dash or two. FLEX_SCREEN_PORT picks another port (48152 talks to a
# companion playing with the simulator).
#
# Exit codes: 0 done (ask: an option was tapped), 1 ask dismissed on the deck, 2 ask not
# answered in time, 3 the companion or the deck isn't there, 4 wrong arguments.
$ErrorActionPreference = 'Stop'

function Show-Usage {
  [Console]::Error.WriteLine(@'
flexctl: notes, questions and progress on the flex-screen deck.

  flexctl notify TEXT [-level info|done|warning|error] [-from NAME]
  flexctl ask QUESTION OPTION OPTION [OPTION OPTION] [-wait MINUTES] [-from NAME]
  flexctl progress DONE TOTAL [LABEL] [-from NAME]
  flexctl progress -clear

ask prints the option tapped. Exit codes: 0 done, 1 dismissed, 2 no answer in time,
3 the companion or the deck isn't there, 4 wrong arguments.
'@)
  exit 4
}

# The words, and the -name value options (one dash or two; -clear and -help take none).
$words = New-Object System.Collections.Generic.List[string]
$opts = @{}
for ($i = 0; $i -lt $args.Count; $i++) {
  $a = [string]$args[$i]
  if ($a -match '^--?([A-Za-z]+)$') {
    $name = $Matches[1].ToLowerInvariant()
    if ($name -in 'clear', 'help', 'h') { $opts[$name] = $true; continue }
    if ($i + 1 -ge $args.Count) { [Console]::Error.WriteLine("flexctl: -$name needs a value"); exit 4 }
    $opts[$name] = [string]$args[++$i]
  } else {
    $words.Add($a)
  }
}
if ($words.Count -eq 0 -or $opts.ContainsKey('help') -or $opts.ContainsKey('h')) { Show-Usage }
$command = $words[0].ToLowerInvariant()
$rest = @($words | Select-Object -Skip 1)
$port = if ($env:FLEX_SCREEN_PORT) { $env:FLEX_SCREEN_PORT } else { '48151' }

function Send-Deck([string]$path, $body) {
  $request = [System.Net.HttpWebRequest]::Create("http://localhost:$port/api/$path")
  $request.Method = 'POST'
  $request.ContentType = 'application/json; charset=utf-8'
  $request.Proxy = $null
  $request.Timeout = 10000
  $request.ReadWriteTimeout = 60000  # a question's reply sends a space every 10 s while it waits
  $bytes = [System.Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress))
  $request.ContentLength = $bytes.Length
  try {
    $stream = $request.GetRequestStream()
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Close()
    $response = $request.GetResponse()
  } catch {
    $e = $_.Exception
    while ($e -and -not ($e -is [System.Net.WebException])) { $e = $e.InnerException }
    if (-not $e -or -not $e.Response) {
      [Console]::Error.WriteLine("flexctl: the flex-screen companion isn't running (nothing answers on localhost:$port)")
      exit 3
    }
    $response = $e.Response
  }
  $reader = New-Object System.IO.StreamReader($response.GetResponseStream(), [System.Text.Encoding]::UTF8)
  $text = $reader.ReadToEnd()
  $response.Close()
  $reply = if ($text.Trim()) { $text.Trim() | ConvertFrom-Json } else { $null }
  $status = [int]$response.StatusCode
  if ($status -ge 400) {
    $why = if ($reply -and $reply.error) { $reply.error } else { "HTTP $status" }
    [Console]::Error.WriteLine("flexctl: $why")
    exit $(if ($status -eq 503) { 3 } else { 4 })
  }
  return $reply
}

$body = @{}
if ($opts.ContainsKey('from')) { $body.from = $opts.from }

switch ($command) {
  'notify' {
    if ($rest.Count -lt 1) { Show-Usage }
    $body.text = $rest -join ' '
    if ($opts.ContainsKey('level')) { $body.level = $opts.level }
    Send-Deck 'notify' $body | Out-Null
    exit 0
  }
  'ask' {
    if ($rest.Count -lt 3 -or $rest.Count -gt 5) { Show-Usage }
    $body.question = $rest[0]
    $body.options = @($rest | Select-Object -Skip 1)
    if ($opts.ContainsKey('wait')) {
      $minutes = 0.0
      if (-not [double]::TryParse($opts.wait, [ref]$minutes)) { [Console]::Error.WriteLine('flexctl: -wait takes minutes'); exit 4 }
      $body.wait_minutes = $minutes
    }
    $reply = Send-Deck 'ask' $body
    switch ($reply.outcome) {
      'picked' { [Console]::Out.WriteLine($reply.answer); exit 0 }
      'dismissed' { [Console]::Error.WriteLine('flexctl: dismissed on the deck'); exit 1 }
      default { [Console]::Error.WriteLine('flexctl: no answer on the deck in time'); exit 2 }
    }
  }
  'progress' {
    if ($opts.ContainsKey('clear')) {
      $body.clear = $true
    } else {
      $done = 0.0; $total = 0.0
      if ($rest.Count -lt 2 -or -not [double]::TryParse($rest[0], [ref]$done) -or -not [double]::TryParse($rest[1], [ref]$total)) { Show-Usage }
      $body.done = $done
      $body.total = $total
      if ($rest.Count -gt 2) { $body.label = (@($rest | Select-Object -Skip 2)) -join ' ' }
    }
    Send-Deck 'progress' $body | Out-Null
    exit 0
  }
  default { Show-Usage }
}
