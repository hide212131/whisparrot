<#
.SYNOPSIS
  Publish whisparrot as a single exe (run on Windows; needs .NET SDK 10 and the VC++ redist folder).
.PARAMETER Arch
  arm64 | x64 | all
.PARAMETER Backend
  cpu (default) | vulkan (x64 only). CUDA is out of scope for v1.
.PARAMETER Out
  Output root (outside the repository is recommended). Default: out\<rid>\ under the repo.
#>
param(
    [ValidateSet('arm64', 'x64', 'all')][string]$Arch = 'all',
    [ValidateSet('cpu', 'vulkan')][string]$Backend = 'cpu',
    [string]$Out = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Locate <VS>\VC\Redist\MSVC\<version> (contains x64\ and arm64\ with Microsoft.VC*.CRT).
function Find-VcRedist {
    if ($env:VCToolsRedistDir) { return $env:VCToolsRedistDir.TrimEnd('\') }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $inst = & $vswhere -latest -products * -property installationPath
        if ($inst) {
            $redist = Get-ChildItem (Join-Path $inst 'VC\Redist\MSVC') -Directory -ErrorAction SilentlyContinue |
                Where-Object { Test-Path (Join-Path $_.FullName 'x64') } | Sort-Object Name -Descending | Select-Object -First 1
            if ($redist) { return $redist.FullName }
        }
    }
    return $null
}

$vc = Find-VcRedist
if (-not $vc) { Write-Warning 'VC++ redist folder not found: the VC++ runtime will NOT be bundled.' }

$rids = if ($Arch -eq 'all') { @('win-arm64', 'win-x64') } else { @("win-$Arch") }
foreach ($rid in $rids) {
    if ($Backend -eq 'vulkan' -and $rid -ne 'win-x64') { Write-Host "skip $rid (vulkan is x64 only)"; continue }
    $dest = if ($Out) { Join-Path $Out $rid } else { Join-Path $root "out\$rid" }
    $publishArgs = @('publish', (Join-Path $root 'src\Whisparrot.App'), '-c', 'Release', '-r', $rid, "-p:Backend=$Backend", '-o', $dest)
    if ($vc) { $publishArgs += "-p:VcRedistDir=$vc" }
    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $rid" }
    $exe = Join-Path $dest 'whisparrot.exe'
    $len = (Get-Item $exe).Length
    Write-Host ("{0}: {1:N0} bytes ({2:N1} MiB)" -f $rid, $len, ($len / 1MB))
}
