<#
.SYNOPSIS
  Loopback mock of an OpenAI-compatible POST /v1/audio/transcriptions endpoint, for the cloud-transcription checks (no real service involved).
  Development only: it is never shipped. The behaviour is chosen per request from the first line of -ModeFile, so a driver can switch cases
  without restarting.
.DESCRIPTION
  Modes: ok | empty | notext | nulltext | numtext | malformed | bigresp | http401 | http403 | http404 | http413 | http429 | http500 | http503
         | echo401 | redirect | hang | stall | hangfirst
  hang / stall / hangfirst answer from a background runspace (hang: never answers within 75 s; stall: sends part of a body, then stops;
  hangfirst: the first request hangs, later ones succeed), so the listener keeps accepting other requests.
  Every request is appended to -LogPath as one JSON line: method, path, auth (absent|match|mismatch, compared with -ExpectKey), the multipart
  field names, filename, part media type, model, and a WAV summary (RIFF/fmt/data checks, sample count, peak). The audio and the key are never
  written to the log or the console.
#>
param(
    [Parameter(Mandatory)][int]$Port,
    [Parameter(Mandatory)][string]$ModeFile,
    [Parameter(Mandatory)][string]$LogPath,
    [string]$ExpectKey = ''
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$latin1 = [System.Text.Encoding]::GetEncoding(28591)
$ResponseText = 'こんにちは。これはクラウド認識のテスト音声です。'
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
$script:count = 0
$script:background = New-Object System.Collections.ArrayList

function Send($ctx, [int]$status, [string]$body) {
    $bytes = $utf8.GetBytes($body)
    $ctx.Response.StatusCode = $status
    $ctx.Response.ContentType = 'application/json; charset=utf-8'
    $ctx.Response.ContentLength64 = $bytes.Length
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $ctx.Response.Close()
}

function Text-Json($value) { ConvertTo-Json -Compress ([ordered]@{ text = $value; language = 'ja'; duration = 1.0 }) }

function Wav-Summary([byte[]]$b) {
    $ok = $b.Length -ge 44 -and $latin1.GetString($b, 0, 4) -eq 'RIFF' -and $latin1.GetString($b, 8, 4) -eq 'WAVE' -and $latin1.GetString($b, 12, 4) -eq 'fmt ' -and $latin1.GetString($b, 36, 4) -eq 'data'
    if (-not $ok) { return [ordered]@{ ok = $false; length = $b.Length } }
    $dataLen = [BitConverter]::ToInt32($b, 40)
    $fmt = [BitConverter]::ToInt32($b, 16) -eq 16 -and [BitConverter]::ToInt16($b, 20) -eq 1 -and [BitConverter]::ToInt16($b, 22) -eq 1 -and [BitConverter]::ToInt32($b, 24) -eq 16000 -and
        [BitConverter]::ToInt32($b, 28) -eq 32000 -and [BitConverter]::ToInt16($b, 32) -eq 2 -and [BitConverter]::ToInt16($b, 34) -eq 16
    $sizes = ($dataLen -eq $b.Length - 44) -and ([BitConverter]::ToInt32($b, 4) -eq $b.Length - 8)
    $peak = -1
    if ($dataLen -le 400000) {   # the peak is only computed for short fixtures (a loop over 12 M samples would be slow)
        $peak = 0
        for ($i = 44; $i + 1 -lt $b.Length; $i += 2) { $v = [Math]::Abs([int][BitConverter]::ToInt16($b, $i)); if ($v -gt $peak) { $peak = $v } }
    }
    [ordered]@{ ok = ($fmt -and $sizes); length = $b.Length; samples = [int]($dataLen / 2); peak = $peak }
}

function Parse-Multipart($req, [byte[]]$bytes) {
    $result = [ordered]@{ fields = @(); filename = $null; part_type = $null; model = $null; wav = $null }
    if (-not ($req.ContentType -match 'boundary="?([^";]+)"?')) { return $result }
    $delim = '--' + $Matches[1]
    $text = $latin1.GetString($bytes)
    $parts = $text.Split(@($delim), [StringSplitOptions]::None)
    foreach ($p in $parts) {
        $split = $p.IndexOf("`r`n`r`n")
        if ($split -lt 0) { continue }
        $head = $p.Substring(0, $split)
        $body = $p.Substring($split + 4)
        if ($body.EndsWith("`r`n")) { $body = $body.Substring(0, $body.Length - 2) }
        if (-not ($head -match 'name="([^"]*)"')) { continue }
        $name = $Matches[1]
        $result.fields += $name
        if ($head -match 'filename="([^"]*)"') { $result.filename = $Matches[1] }
        if ($head -match '(?im)^Content-Type:\s*(\S+)') { $result.part_type = $Matches[1] }
        $raw = $latin1.GetBytes($body)
        if ($name -eq 'model') { $result.model = $utf8.GetString($raw) }
        elseif ($name -eq 'file') { $result.wav = Wav-Summary $raw }
    }
    return $result
}

$slowScript = {
    param($ctx, $mode, $seconds)
    try {
        if ($mode -eq 'stall') {
            $bytes = [System.Text.Encoding]::UTF8.GetBytes('{"text":"par')
            $ctx.Response.StatusCode = 200
            $ctx.Response.ContentType = 'application/json'
            $ctx.Response.ContentLength64 = 4096
            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
            $ctx.Response.OutputStream.Flush()
        }
        Start-Sleep -Seconds $seconds
        $ctx.Response.Abort()
    } catch { }
}

while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    try {
        $req = $ctx.Request
        $ms = New-Object System.IO.MemoryStream
        $req.InputStream.CopyTo($ms)
        $bytes = $ms.ToArray()
        $authHeader = $req.Headers['Authorization']
        $auth = if (-not $authHeader) { 'absent' } elseif ($ExpectKey -and $authHeader -eq "Bearer $ExpectKey") { 'match' } else { 'mismatch' }
        $mp = Parse-Multipart $req $bytes
        $line = [ordered]@{ method = $req.HttpMethod; path = $req.Url.AbsolutePath; auth = $auth; body_bytes = $bytes.Length
            fields = ($mp.fields -join ','); filename = $mp.filename; part_type = $mp.part_type; model = $mp.model; wav = $mp.wav }
        [System.IO.File]::AppendAllText($LogPath, (ConvertTo-Json -Compress -Depth 6 $line) + "`n", $utf8)
        $script:count++

        $mode = (Get-Content -LiteralPath $ModeFile -TotalCount 1).Trim()
        if ($mode -eq 'hangfirst') { $mode = if ($script:count -eq 1) { 'hang' } else { 'ok' } }
        switch ($mode) {
            'ok'        { Send $ctx 200 (Text-Json $ResponseText) }
            'empty'     { Send $ctx 200 (Text-Json "  `n ") }
            'notext'    { Send $ctx 200 '{"language":"ja"}' }
            'nulltext'  { Send $ctx 200 '{"text":null}' }
            'numtext'   { Send $ctx 200 '{"text":5}' }
            'malformed' { Send $ctx 200 '<html><body>gateway</body></html>' }
            'bigresp'   { Send $ctx 200 (Text-Json ('あ' * 400000)) }   # about 1.2 MB of UTF-8, over the 1 MiB limit
            'http401'   { Send $ctx 401 '{"error":{"message":"bad key"}}' }
            'http403'   { Send $ctx 403 '{"error":{"message":"forbidden"}}' }
            'http404'   { Send $ctx 404 '{"error":{"message":"model not found"}}' }
            'http413'   { Send $ctx 413 '{"error":{"message":"too large"}}' }
            'http429'   { $ctx.Response.AddHeader('Retry-After', '1'); Send $ctx 429 '{"error":{"message":"rate limited"}}' }
            'http500'   { Send $ctx 500 '{"error":{"message":"internal"}}' }
            'http503'   { Send $ctx 503 '{"error":{"message":"unavailable"}}' }
            'echo401'   { Send $ctx 401 ("echo " + $authHeader + " model=" + $mp.model + " fields=" + ($mp.fields -join ',')) }
            'redirect'  {
                $ctx.Response.StatusCode = 307
                $ctx.Response.RedirectLocation = "http://127.0.0.1:$Port/redirected/v1/audio/transcriptions"
                $ctx.Response.Close()
            }
            { $_ -in 'hang', 'stall' } {
                $ps = [powershell]::Create()
                [void]$ps.AddScript($slowScript).AddArgument($ctx).AddArgument($_).AddArgument(75)
                [void]$ps.BeginInvoke()
                [void]$script:background.Add($ps)
            }
            default     { Send $ctx 500 '{"error":"unknown mode"}' }
        }
    } catch {
        try { $ctx.Response.Abort() } catch { }
    }
}
