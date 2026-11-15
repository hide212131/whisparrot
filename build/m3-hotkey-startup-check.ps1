<#
.SYNOPSIS
  Starts the real tray exe from a clean directory (no config.json) and checks that the hotkey negotiation result is only
  reported (hotkey_adopted=) and never written to config.json. In a non-interactive session every RegisterHotKey fails
  (ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION), which still exercises the "negotiated key differs from the configured key" path.
#>
param([Parameter(Mandatory)][string]$Exe, [string]$Work = (Join-Path $env:TEMP 'whisparrot-m3-hotkey'))
$ErrorActionPreference = 'Stop'
Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Work | Out-Null
Copy-Item $Exe (Join-Path $Work 'whisparrot.exe')
$stdout = Join-Path $Work 'out.txt'
$p = Start-Process (Join-Path $Work 'whisparrot.exe') -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError (Join-Path $Work 'err.txt')
Start-Sleep -Seconds 10
$alive = -not $p.HasExited
if ($alive) { Stop-Process -Id $p.Id -Force }
$out = Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue
Write-Host $out
$configExists = Test-Path (Join-Path $Work 'config.json')
Write-Host "process_stayed_up=$alive config_json_exists=$configExists hotkey_persisted_printed=$($out -match 'hotkey_persisted')"
if (-not $alive -or $configExists -or ($out -match 'hotkey_persisted') -or -not ($out -match 'hotkey_adopted=')) { Write-Host 'RESULT: FAIL'; exit 1 }
Write-Host 'RESULT: PASS'
