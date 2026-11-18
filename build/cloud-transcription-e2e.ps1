<#
.SYNOPSIS
  Cloud-transcription end-to-end check on Windows: the real exe (--transcription-probe and --pipeline-file --use-settings, i.e. the real
  SettingsRecognizer / CloudTranscriber / NetworkContext / SessionCoordinator) against the loopback mocks. No real service is contacted.
  The API key is a dummy string whose only purpose is to prove it never shows up in the exe's output. Whisper is NOT used as a stand-in for
  the HTTP path, and no local model is needed (the exe's folder has no models\).
.PARAMETER Exe       published whisparrot.exe (arm64 or x64)
.PARAMETER SkipLong  skip the cases that wait for the real 60 s deadline (hang / stall) and the cancel check that follows them
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [int]$Port = 18770,
    [int]$CompatPort = 18771,
    [string]$Work = (Join-Path $env:TEMP 'whisparrot-ct3-e2e'),
    [switch]$SkipLong
)
$ErrorActionPreference = 'Stop'
$dummyKey = 'dummy-key-NOT-A-REAL-SECRET-0123456789'
$cloudText = 'こんにちは。これはクラウド認識のテスト音声です。'
$polished = '明日は3時に会議です。'
New-Item -ItemType Directory -Force $Work | Out-Null
$modeFile = Join-Path $Work 'mode.txt'
$compatMode = Join-Path $Work 'compat-mode.txt'
$log = Join-Path $Work 'requests.jsonl'
$compatLog = Join-Path $Work 'compat-requests.jsonl'
$cfg = Join-Path $Work 'config.json'
$tone = Join-Path $Work 'tone.wav'
$big = Join-Path $Work 'too-long.wav'
$limit = Join-Path $Work 'at-limit.wav'
$env:WHISPARROT_E2E_KEY = $dummyKey
$script:fail = 0
$script:total = 0
$script:mocks = @{}

function Check([string]$name, [bool]$pass) { $script:total++; if (-not $pass) { $script:fail++ }; Write-Host ("{0} {1}" -f ($(if ($pass) { 'PASS' } else { 'FAIL' })), $name) }

function New-Wav([string]$path, [int]$samples, [bool]$sine) {
    $fs = [System.IO.File]::Create($path)
    try {
        $w = New-Object System.IO.BinaryWriter($fs)
        $dataLen = $samples * 2
        $w.Write([byte[]][char[]]'RIFF'); $w.Write([int](36 + $dataLen)); $w.Write([byte[]][char[]]'WAVEfmt ')
        $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1); $w.Write([int]16000); $w.Write([int]32000); $w.Write([int16]2); $w.Write([int16]16)
        $w.Write([byte[]][char[]]'data'); $w.Write([int]$dataLen)
        if ($sine) {
            for ($i = 0; $i -lt $samples; $i++) { $w.Write([int16][Math]::Round(16384 * [Math]::Sin(2 * [Math]::PI * 440 * $i / 16000))) }
        } else {
            $zero = New-Object byte[] 1000000
            $left = $dataLen
            while ($left -gt 0) { $n = [Math]::Min($left, $zero.Length); $w.Write($zero, 0, $n); $left -= $n }
        }
        $w.Flush()
    } finally { $fs.Dispose() }
}

function Start-Mock([string]$name, [string]$script, [int]$port, [string]$mode, [string]$logPath, [string]$modePath) {
    Set-Content $modePath $mode
    Remove-Item $logPath -ErrorAction SilentlyContinue
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot $script), '-Port', $port, '-ModeFile', $modePath, '-LogPath', $logPath, '-ExpectKey', $dummyKey)
    $p = Start-Process powershell -ArgumentList $a -PassThru -WindowStyle Hidden
    $script:mocks[$name] = $p
    for ($i = 0; $i -lt 50; $i++) {
        try { $c = New-Object System.Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $port); $c.Close(); return } catch { Start-Sleep -Milliseconds 200 }
    }
    throw "mock $name did not start"
}
function Stop-Mock([string]$name) {
    $p = $script:mocks[$name]
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    $script:mocks.Remove($name)
}
function Start-Cloud([string]$mode) { Start-Mock 'cloud' 'mock-transcription-server.ps1' $Port $mode $log $modeFile }
function Restart-Cloud([string]$mode) { Stop-Mock 'cloud'; Start-Cloud $mode }
function Start-Compat([string]$mode) { Start-Mock 'compat' 'mock-compat-server.ps1' $CompatPort $mode $compatLog $compatMode }

function Requests([string]$path = $log) { if (Test-Path $path) { @(Get-Content -LiteralPath $path -Encoding UTF8 | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }) } else { @() } }
function Hits([string]$path = $log) { (Requests $path).Count }
function Clear-Logs { Remove-Item $log, $compatLog -ErrorAction SilentlyContinue }

function Write-Config([string]$url, [string]$model, [bool]$withKey, [string]$engine = 'cloud') {
    Remove-Item $cfg -ErrorAction SilentlyContinue
    $a = @('--write-transcription-config', $cfg, '--url', $url, '--model', $model, '--engine', $engine)
    if ($withKey) { $a += @('--key-env', 'WHISPARROT_E2E_KEY') }
    & $Exe @a 2>&1 | Out-String | Out-Null
}
function Add-Polish([string]$provider = 'compatible') {
    & $Exe --write-cleanup-config $cfg --url "http://127.0.0.1:$CompatPort/v1" --model polish-model --key-env WHISPARROT_E2E_KEY --provider $provider 2>&1 | Out-String | Out-Null
}

function Run-Probe([string]$wav, [string[]]$extra = @()) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = & $Exe --transcription-probe --config $cfg --wav $wav @extra 2>&1 | Out-String
    [pscustomobject]@{ Code = $LASTEXITCODE; Text = $out; Ms = $sw.ElapsedMilliseconds }
}
function Run-Pipeline([string]$wav, [string[]]$extra = @()) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = & $Exe --pipeline-file $wav --use-settings --config $cfg --show-text @extra 2>&1 | Out-String
    [pscustomobject]@{ Code = $LASTEXITCODE; Text = $out; Ms = $sw.ElapsedMilliseconds }
}
function Injected($r) { @($r.Text -split "`r?`n" | Where-Object { $_ -like 'injected_text=*' } | ForEach-Object { $_ -replace '^injected_text=', '' }) }
function Kv($r, [string]$key) { if ($r.Text -match ("(?m)(?:^|\s)" + [regex]::Escape($key) + "=(\S+)")) { $Matches[1] } else { $null } }
function States($r) { (@($r.Text -split "`r?`n" | ForEach-Object { if ($_ -match 'state=(\w+)') { $Matches[1] } }) -join '>') }
function No-Leak($r) { -not $r.Text.Contains($dummyKey) -and -not $r.Text.Contains('echo Bearer') -and -not $r.Text.Contains('model=') }

New-Wav $tone 48000 $true
New-Wav $big 12499979 $false     # 44 + 2N = 25,000,002 byte: one sample over the limit
New-Wav $limit 12499978 $false   # 25,000,000 byte: exactly the limit
$url = "http://127.0.0.1:$Port/v1"
Write-Host "exe: $Exe"
Write-Host "mock: $url (compat mock on $CompatPort)"
try {
    Start-Cloud 'ok'

    # --- config tool: only recognition is written; the key is encrypted, never printed ---
    $w = & $Exe --write-transcription-config $cfg --url $url --model 'mock-stt' --key-env WHISPARROT_E2E_KEY 2>&1 | Out-String
    Check 'write-transcription-config: engine=cloud, key stored via dpapi, no key in output or in config.json' ($LASTEXITCODE -eq 0 -and $w -match 'engine=cloud' -and $w -match 'key=dpapi' -and -not $w.Contains($dummyKey) -and -not (Get-Content $cfg -Raw).Contains($dummyKey))

    # --- probe: success, outcome only ---
    Clear-Logs
    $r = Run-Probe $tone
    $q = @(Requests)
    Check 'probe ok: exit 0, outcome=success, text_chars=<N>, destination host shown, injected_count=0' ($r.Code -eq 0 -and (Kv $r 'recognition_outcome') -eq 'success' -and (Kv $r 'text_chars') -eq "$($cloudText.Length)" -and $r.Text -match 'destination_host=127.0.0.1:' -and (Kv $r 'injected_count') -eq '0')
    Check 'probe ok: the transcribed text and the key are NOT in the output' (-not $r.Text.Contains($cloudText) -and -not $r.Text.Contains($dummyKey) -and $r.Text -notmatch '(?m)^text=')
    Check 'probe ok: exactly one POST to /v1/audio/transcriptions with the matching Bearer' ($q.Count -eq 1 -and $q[0].method -eq 'POST' -and $q[0].path -eq '/v1/audio/transcriptions' -and $q[0].auth -eq 'match')
    Check 'probe ok: multipart has file + model only; speech.wav, audio/wav, model verbatim' ($q[0].fields -eq 'file,model' -and $q[0].filename -eq 'speech.wav' -and $q[0].part_type -eq 'audio/wav' -and $q[0].model -eq 'mock-stt')
    Check 'probe ok: the WAV is RIFF PCM16 16 kHz mono (44-byte header), 48000 samples, peak near the fixture peak' ($q[0].wav.ok -eq $true -and $q[0].wav.samples -eq 48000 -and $q[0].wav.length -eq 96044 -and [Math]::Abs($q[0].wav.peak - 16383) -le 4)
    $r = Run-Probe $tone @('--show-text')
    Check 'probe --show-text prints the text (fixture only)' ($r.Code -eq 0 -and $r.Text.Contains("text=$cloudText"))

    # --- probe: failure classification (one request each, no retry, nothing leaks) ---
    $expect = [ordered]@{
        http401 = @('Auth', '401'); http403 = @('Auth', '403'); http404 = @('Http', '404'); http413 = @('PayloadTooLarge', '413'); http429 = @('Http', '429')
        http500 = @('Http', '500'); http503 = @('Http', '503'); malformed = @('Malformed', '-'); notext = @('Malformed', '-'); nulltext = @('Malformed', '-')
        numtext = @('Malformed', '-'); bigresp = @('ResponseTooLarge', '-'); redirect = @('Http', '307'); echo401 = @('Auth', '401')
    }
    foreach ($mode in $expect.Keys) {
        Set-Content $modeFile $mode; Clear-Logs
        $r = Run-Probe $tone
        $kind = Kv $r 'failure_kind'; $status = Kv $r 'http_status'
        $redirectHits = @(Requests | Where-Object { $_.path -like '/redirected/*' }).Count
        Check "probe $mode : failed as $($expect[$mode][0]) / $($expect[$mode][1]), exit non-zero, 1 request (no retry), no leak" (
            $r.Code -eq 1 -and (Kv $r 'recognition_outcome') -eq 'failed' -and $kind -eq $expect[$mode][0] -and $status -eq $expect[$mode][1] -and (Hits) -eq 1 -and $redirectHits -eq 0 -and (No-Leak $r))
    }
    Set-Content $modeFile 'empty'; Clear-Logs
    $r = Run-Probe $tone
    Check 'probe empty text: outcome=empty (not an error), exit 0, text_chars=0' ($r.Code -eq 0 -and (Kv $r 'recognition_outcome') -eq 'empty' -and (Kv $r 'text_chars') -eq '0' -and (Hits) -eq 1)
    Stop-Mock 'cloud'
    Remove-Item $log -ErrorAction SilentlyContinue
    $r = Run-Probe $tone
    Check 'probe connection refused: failure_kind=Network, exit non-zero' ($r.Code -eq 1 -and (Kv $r 'failure_kind') -eq 'Network' -and (No-Leak $r))

    # --- pipeline: success, polish OFF, no local model ---
    Start-Cloud 'ok'; Clear-Logs
    Write-Config $url 'mock-stt' $true
    $r = Run-Pipeline $tone
    $q = @(Requests)
    Check 'pipeline ok (polish OFF, no local model): exit 0, text injected exactly once' ($r.Code -eq 0 -and (Injected $r).Count -eq 1 -and (Injected $r)[0] -eq $cloudText -and (Kv $r 'injected_count') -eq '1')
    Check 'pipeline ok: Recording>Recognizing>Inserting>Idle, backend=cloud, no local model resolution' ((States $r) -eq 'Recording>Recognizing>Inserting>Idle' -and $r.Text -match 'backend=cloud' -and $r.Text -notmatch 'model_id=')
    Check 'pipeline ok: one POST, model and Bearer as configured, no key in output' ($q.Count -eq 1 -and $q[0].auth -eq 'match' -and $q[0].model -eq 'mock-stt' -and (No-Leak $r))
    $r2 = Run-Pipeline $tone @('--repeat', '3'); 
    Check 'pipeline ok x3 in one process: 3 injections, 3 more requests (one per utterance)' ($r2.Code -eq 0 -and (Injected $r2).Count -eq 3 -and (Hits) -eq 4)
    Clear-Logs
    $r = Run-Pipeline $limit
    $q = @(Requests)
    Check 'pipeline at the size limit (25,000,000 byte WAV): sent and succeeded' ($r.Code -eq 0 -and $q.Count -eq 1 -and $q[0].wav.ok -eq $true -and $q[0].wav.length -eq 25000000 -and $q[0].wav.samples -eq 12499978)

    # --- pipeline: cloud text flows into the existing polish ---
    Start-Compat 'ok'; Clear-Logs
    Add-Polish
    $r = Run-Pipeline $tone
    $cq = @(Requests $compatLog); $q = @(Requests)
    $sent = if ($cq.Count -ge 1) { ($cq[0].body | ConvertFrom-Json).messages[1].content } else { '' }
    Check 'pipeline ok + compat polish: the cloud text is the polish input, only the polished text is injected once' ($r.Code -eq 0 -and (Injected $r).Count -eq 1 -and (Injected $r)[0] -eq $polished -and $sent -eq $cloudText -and (States $r) -eq 'Recording>Recognizing>Polishing>Inserting>Idle')
    Check 'pipeline ok + polish: the transcription endpoint got 1 audio POST and no chat request; the polish endpoint got no audio request' ($q.Count -eq 1 -and $q[0].path -eq '/v1/audio/transcriptions' -and $cq.Count -eq 1 -and $cq[0].path -eq '/v1/chat/completions')

    # --- polish failure after a successful recognition: the cloud text itself is injected once ---
    foreach ($m in 'http500') {
        Set-Content $compatMode $m; Clear-Logs
        $r = Run-Pipeline $tone
        Check "pipeline ok + polish $m : the transcribed text injected exactly once with a warning, 1 audio request" ($r.Code -eq 0 -and (Injected $r).Count -eq 1 -and (Injected $r)[0] -eq $cloudText -and $r.Text -match '\[notice:Warning\] 整文できなかったため' -and (Hits) -eq 1)
    }
    Stop-Mock 'compat'; Start-Compat 'hang'; Clear-Logs
    $r = Run-Pipeline $tone
    $ms = [int](Kv $r 'run1_total_ms')
    Check "pipeline ok + polish hang: the 15 s polish deadline (${ms} ms) returns the transcribed text once; recognition was not delayed" ($r.Code -eq 0 -and $ms -ge 14500 -and $ms -le 18000 -and (Injected $r).Count -eq 1 -and (Injected $r)[0] -eq $cloudText -and $r.Text -match '時間切れ')
    Stop-Mock 'compat'; Start-Compat 'ok'

    # --- pipeline: recognition failures never inject, never polish, never fall back to local ---
    $notice = [ordered]@{
        http401 = '認証エラー'; http403 = '認証エラー'; http404 = 'HTTP 404'; http413 = '音声が接続先の上限を超えています'; http429 = 'HTTP 429'; http500 = 'HTTP 500'
        malformed = '応答を解釈できませんでした'; notext = '応答を解釈できませんでした'; nulltext = '応答を解釈できませんでした'; bigresp = '応答を解釈できませんでした'
        redirect = 'HTTP 307'; echo401 = '認証エラー'; empty = '音声を認識できませんでした'
    }
    foreach ($mode in $notice.Keys) {
        Set-Content $modeFile $mode; Clear-Logs
        $r = Run-Pipeline $tone
        $redirectHits = @(Requests | Where-Object { $_.path -like '/redirected/*' }).Count
        $okEnd = (States $r) -match '^Recording>Recognizing>Idle$'
        Check "pipeline $mode : nothing injected, Idle, notice '$($notice[$mode])', no polish request, 1 audio request, no redirect follow, no leak" (
            $r.Code -eq 1 -and (Kv $r 'injected_count') -eq '0' -and $okEnd -and $r.Text.Contains($notice[$mode]) -and (Hits $compatLog) -eq 0 -and (Hits) -eq 1 -and $redirectHits -eq 0 -and (No-Leak $r) -and $r.Text -notmatch 'state=Failed')
    }
    Stop-Mock 'cloud'
    $r = Run-Pipeline $tone
    Check 'pipeline connection refused: nothing injected, 接続エラー, no polish request' ($r.Code -eq 1 -and (Kv $r 'injected_count') -eq '0' -and $r.Text.Contains('接続エラー') -and (Hits $compatLog) -eq 0 -and (No-Leak $r))

    # --- not started: zero cloud requests, no fallback to the local engine ---
    Start-Cloud 'ok'; Clear-Logs
    Write-Config $url 'mock-stt' $true 'local'
    $r = Run-Pipeline $tone
    Check 'local selected (cloud settings kept): no cloud request; the local model is missing so nothing runs and nothing is injected' ($r.Code -eq 1 -and (Hits) -eq 0 -and (Kv $r 'recording_started') -eq 'no' -and (Kv $r 'injected_count') -eq '0' -and $r.Text -notmatch 'backend=cloud')
    Write-Config 'https://transcription.example.invalid/v1' 'mock-stt' $false
    $r = Run-Pipeline $tone
    Check 'cloud selected, external host, no key: unconfigured, 0 requests, not started, no local fallback' ($r.Code -eq 1 -and (Hits) -eq 0 -and (Kv $r 'recording_started') -eq 'no' -and $r.Text.Contains('API キーが未設定') -and (Kv $r 'injected_count') -eq '0')
    $r = Run-Probe $tone
    Check 'probe, same config: outcome=unconfigured, no request' ($r.Code -eq 1 -and (Kv $r 'recognition_outcome') -eq 'unconfigured' -and (Hits) -eq 0)
    Write-Config $url 'mock-stt' $true
    $json = Get-Content $cfg -Raw
    $broken = [regex]::Replace($json, '("api_key_dpapi"\s*:\s*")[^"]+(")', '${1}AAAAAAAAAAAAAAAAAAAAAAAA${2}')
    [System.IO.File]::WriteAllText($cfg, $broken, (New-Object System.Text.UTF8Encoding($false)))
    $before = (Get-FileHash $cfg).Hash
    $r = Run-Pipeline $tone
    Check 'undecryptable key: 0 requests, not started, no local fallback, ciphertext left in place (config.json unchanged)' ($broken -ne $json -and $r.Code -eq 1 -and (Hits) -eq 0 -and (Kv $r 'recording_started') -eq 'no' -and $r.Text.Contains('復号できません') -and (Get-FileHash $cfg).Hash -eq $before)
    Write-Config $url 'mock-stt' $true
    $r = Run-Pipeline $big
    Check 'one sample over the size limit (25,000,002 byte WAV): 0 requests, nothing injected, fixed notice, Idle' ($r.Code -eq 1 -and (Hits) -eq 0 -and (Kv $r 'injected_count') -eq '0' -and $r.Text.Contains('録音が長すぎる') -and (States $r) -eq 'Recording>Recognizing>Idle')
    $r = Run-Probe $big
    Check 'probe over the size limit: failure_kind=AudioTooLong, 0 requests' ($r.Code -eq 1 -and (Kv $r 'failure_kind') -eq 'AudioTooLong' -and (Hits) -eq 0)

    # --- cancel while the server is waiting, then the next session succeeds (no retry) ---
    Restart-Cloud 'hangfirst'; Clear-Logs
    $r = Run-Pipeline $tone @('--cancel-after-ms', '500', '--cancel-first-only', '--repeat', '2')
    Check 'cancel during recognition: Idle, nothing injected for it, no notice, no retry; the next session succeeds (2 requests in total)' (
        $r.Code -eq 0 -and $r.Text -match 'cancelled_state=Idle injected=0' -and (Injected $r).Count -eq 1 -and $r.Text -notmatch '\[notice:' -and (Hits) -eq 2 -and $r.Text -notmatch 'leftover_worker_processes=[1-9]')

    if (-not $SkipLong) {
        # --- real 60 s deadline (allowance 60 s + 2 s clean-up) ---
        foreach ($mode in 'hang', 'stall') {
            Restart-Cloud $mode; Clear-Logs
            Add-Polish
            $r = Run-Pipeline $tone
            $ms = [int](Kv $r 'run1_total_ms')
            Check "pipeline $mode : the recognition ends at the real 60 s deadline (${ms} ms), 時間切れ, nothing injected, no polish request, Idle" (
                $r.Code -eq 1 -and $ms -ge 59500 -and $ms -le 62000 -and $r.Text.Contains('時間切れ') -and (Kv $r 'injected_count') -eq '0' -and (Hits $compatLog) -eq 0 -and (Hits) -eq 1 -and (States $r) -eq 'Recording>Recognizing>Idle')
        }
        Restart-Cloud 'stall'; Clear-Logs
        $r = Run-Probe $tone
        Check "probe stall (body stops after the headers): failure_kind=Timeout at about 60 s ($($r.Ms) ms incl. start-up), no leak" ($r.Code -eq 1 -and (Kv $r 'failure_kind') -eq 'Timeout' -and [int](Kv $r 'elapsed_ms') -ge 59500 -and [int](Kv $r 'elapsed_ms') -le 62000 -and (No-Leak $r))
    }
}
finally {
    foreach ($n in @($script:mocks.Keys)) { Stop-Mock $n }
    Remove-Item Env:WHISPARROT_E2E_KEY -ErrorAction SilentlyContinue
}
Write-Host ("CHECKS: {0} total, {1} failed" -f $script:total, $script:fail)
Write-Host ("RESULT: {0}" -f $(if ($script:fail -eq 0) { 'ALL PASS' } else { "$($script:fail) FAILED" }))
exit $script:fail
