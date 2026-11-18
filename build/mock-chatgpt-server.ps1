<#
.SYNOPSIS
  Loopback mock of the Sign in with ChatGPT pieces used by T13 (token endpoint + Responses API). No real service is involved.
  The behaviour is chosen per request from the first line of -ModeFile, so a driver can switch cases without restarting.
.DESCRIPTION
  POST /api/accounts/oauth/token   form body; grant_type authorization_code | refresh_token
      modes: ok | short (expires_in=60, so the next use refreshes) | noscope (grant lacks chatgpt.tokens.use.direct)
             reject (HTTP 400 invalid_grant) | norotate (refresh keeps the old refresh token) | http503
  POST /v1/responses               Bearer must equal the most recently issued access token (else 401)
      modes: ok | http500 | empty | malformed | hang
  Every request is appended to -LogPath as one JSON line. Token VALUES are never logged: only whether the
  presented refresh_token / Authorization matched what this mock issued last (match|mismatch|absent).
#>
param(
    [Parameter(Mandatory)][int]$Port,
    [Parameter(Mandatory)][string]$ModeFile,
    [Parameter(Mandatory)][string]$LogPath
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
$issued = 0
$scopeFull = 'openid profile email offline_access resource.invoke chatgpt.tokens.use.direct'

function Send($ctx, [int]$status, [string]$body) {
    $bytes = $utf8.GetBytes($body)
    $ctx.Response.StatusCode = $status
    $ctx.Response.ContentType = 'application/json; charset=utf-8'
    $ctx.Response.ContentLength64 = $bytes.Length
    $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $ctx.Response.Close()
}

function Parse-Form([string]$body) {
    $h = @{}
    foreach ($p in $body.Split('&', [StringSplitOptions]::RemoveEmptyEntries)) {
        $kv = $p.Split('=', 2)
        $h[[System.Uri]::UnescapeDataString($kv[0].Replace('+', ' '))] = if ($kv.Length -gt 1) { [System.Uri]::UnescapeDataString($kv[1].Replace('+', ' ')) } else { '' }
    }
    return $h
}

while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    try {
        $req = $ctx.Request
        $body = (New-Object System.IO.StreamReader($req.InputStream, $utf8)).ReadToEnd()
        $mode = (Get-Content -LiteralPath $ModeFile -TotalCount 1).Trim()
        $path = $req.Url.AbsolutePath
        $line = [ordered]@{ method = $req.HttpMethod; path = $path; contentType = $req.ContentType }
        if ($path -eq '/api/accounts/oauth/token') {
            $f = Parse-Form $body
            $line.grant_type = $f['grant_type']
            $line.client_id = $f['client_id']
            $line.resource = $f['resource']
            $line.redirect_uri = $f['redirect_uri']
            $line.has_code = [bool]$f['code']
            $line.has_verifier = [bool]$f['code_verifier']
            if ($f['grant_type'] -eq 'refresh_token') { $line.refresh = if ($f['refresh_token'] -eq "rt-$issued") { 'match' } else { 'mismatch' } }
            [System.IO.File]::AppendAllText($LogPath, (ConvertTo-Json -Compress $line) + "`n", $utf8)
            switch ($mode) {
                'reject' { Send $ctx 400 '{"error":"invalid_grant"}' }
                'http503' { Send $ctx 503 '{"error":"unavailable"}' }
                default {
                    $script:issued++
                    $n = $script:issued
                    $resp = [ordered]@{
                        access_token = "at-$n"; token_type = 'Bearer'
                        expires_in = $(if ($mode -eq 'short') { 60 } else { 3600 })
                        scope = $(if ($mode -eq 'noscope') { 'openid profile email offline_access resource.invoke' } else { $scopeFull })
                    }
                    if (-not ($mode -eq 'norotate' -and $f['grant_type'] -eq 'refresh_token')) { $resp.refresh_token = "rt-$n" }
                    Send $ctx 200 (ConvertTo-Json -Compress $resp)
                }
            }
        }
        elseif ($path -eq '/v1/responses') {
            $auth = $req.Headers['Authorization']
            $line.auth = if (-not $auth) { 'absent' } elseif ($auth -eq "Bearer at-$issued") { 'match' } else { 'mismatch' }
            $line.body = $body
            [System.IO.File]::AppendAllText($LogPath, (ConvertTo-Json -Compress $line) + "`n", $utf8)
            if ($line.auth -ne 'match') { Send $ctx 401 '{"error":"bad token"}'; continue }
            $text = '明日は3時に会議です。'
            switch ($mode) {
                'http500'   { Send $ctx 500 '{"error":"internal"}' }
                'empty'     { Send $ctx 200 '{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"  "}]}]}' }
                'malformed' { Send $ctx 200 '<html>gateway</html>' }
                'hang'      { Start-Sleep -Seconds 16; Send $ctx 200 '{"status":"completed","output":[]}' }
                default {
                    $o = [ordered]@{ id = 'resp_mock'; status = 'completed'; output = @([ordered]@{ type = 'message'; role = 'assistant'; content = @([ordered]@{ type = 'output_text'; text = $text }) }) }
                    Send $ctx 200 (ConvertTo-Json -Compress -Depth 8 $o)
                }
            }
        }
        else {
            [System.IO.File]::AppendAllText($LogPath, (ConvertTo-Json -Compress $line) + "`n", $utf8)
            Send $ctx 404 '{}'
        }
    } catch {
        try { $ctx.Response.Abort() } catch { }
    }
}
