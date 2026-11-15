<#
.SYNOPSIS
  Loopback mock of an OpenAI-compatible /chat/completions endpoint, for the T11 cleanup-client checks (no real endpoint involved).
  The behaviour is chosen per request from the first line of -ModeFile, so a driver can switch cases without restarting.
.DESCRIPTION
  Modes: ok | http500 | http401 | empty | blank | malformed | length | toolcalls | hang | redirect | echo401 | notstop | nocontent
  Every request is appended to -LogPath as one JSON line: method, path, auth (absent|match|mismatch, compared with -ExpectKey), body.
  The key itself is never written to the log or the console.
#>
param(
    [Parameter(Mandatory)][int]$Port,
    [Parameter(Mandatory)][string]$ModeFile,
    [Parameter(Mandatory)][string]$LogPath,
    [string]$ExpectKey = ''
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()

function Send($ctx, [int]$status, [string]$body) {
    $bytes = $utf8.GetBytes($body)
    $ctx.Response.StatusCode = $status
    $ctx.Response.ContentType = 'application/json; charset=utf-8'
    $ctx.Response.ContentLength64 = $bytes.Length
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $ctx.Response.Close()
}

function Completion([string]$content, $finish = 'stop') {
    $c = [ordered]@{ index = 0; message = [ordered]@{ role = 'assistant'; content = $content }; finish_reason = $finish }
    return (ConvertTo-Json -Compress -Depth 8 ([ordered]@{ id = 'mock'; object = 'chat.completion'; choices = @($c) }))
}

while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    try {
        $req = $ctx.Request
        $reader = New-Object System.IO.StreamReader($req.InputStream, $utf8)
        $body = $reader.ReadToEnd()
        $authHeader = $req.Headers['Authorization']
        $auth = if (-not $authHeader) { 'absent' } elseif ($ExpectKey -and $authHeader -eq "Bearer $ExpectKey") { 'match' } else { 'mismatch' }
        $line = [ordered]@{ method = $req.HttpMethod; path = $req.Url.AbsolutePath; auth = $auth; body = $body }
        [System.IO.File]::AppendAllText($LogPath, (ConvertTo-Json -Compress $line) + "`n", $utf8)

        $mode = (Get-Content -LiteralPath $ModeFile -TotalCount 1).Trim()
        switch ($mode) {
            'ok'        { Send $ctx 200 (Completion '明日は3時に会議です。') }
            'http500'   { Send $ctx 500 '{"error":{"message":"internal"}}' }
            'http401'   { Send $ctx 401 '{"error":{"message":"bad key"}}' }
            'echo401'   { Send $ctx 401 ("echo " + $authHeader + " " + $body) }
            'empty'     { Send $ctx 200 (Completion '') }
            'blank'     { Send $ctx 200 (Completion "  `n ") }
            'nocontent' { Send $ctx 200 '{"choices":[{"message":{"role":"assistant"},"finish_reason":"stop"}]}' }
            'malformed' { Send $ctx 200 '<html><body>gateway</body></html>' }
            'length'    { Send $ctx 200 (Completion '途中で切れた本' 'length') }
            'notstop'   { Send $ctx 200 (Completion '終了理由なし' $null) }
            'toolcalls' { Send $ctx 200 '{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"1","type":"function","function":{"name":"f","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}' }
            'hang'      { Start-Sleep -Seconds 16; Send $ctx 200 (Completion '遅すぎた整文') }
            'redirect'  {
                $ctx.Response.StatusCode = 307
                $ctx.Response.RedirectLocation = "http://127.0.0.1:$Port/redirected/v1/chat/completions"
                $ctx.Response.Close()
            }
            default     { Send $ctx 500 '{"error":"unknown mode"}' }
        }
    } catch {
        try { $ctx.Response.Abort() } catch { }
    }
}
