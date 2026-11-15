<#
.SYNOPSIS
  whisparrot の単一 exe を publish する（Windows 上で実行。.NET SDK 10 と VC++ 再頒布 DLL の置き場が必要）。
.PARAMETER Arch
  arm64 | x64 | all
.PARAMETER Backend
  cpu（既定）| vulkan（x64 のみ）。CUDA は初版の対象外。
.PARAMETER Out
  出力先（リポジトリ外を推奨）。既定は out\<rid>\。
#>
param(
    [ValidateSet('arm64', 'x64', 'all')][string]$Arch = 'all',
    [ValidateSet('cpu', 'vulkan')][string]$Backend = 'cpu',
    [string]$Out = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Find-VcRedist {
    if ($env:VCToolsRedistDir) { return (Split-Path -Parent ($env:VCToolsRedistDir.TrimEnd('\'))) | ForEach-Object { $env:VCToolsRedistDir.TrimEnd('\') } }
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
if (-not $vc) { Write-Warning 'VC++ 再頒布 DLL の置き場が見つかりません。VC++ ランタイムは同梱されません。' }

$rids = if ($Arch -eq 'all') { @('win-arm64', 'win-x64') } else { @("win-$Arch") }
foreach ($rid in $rids) {
    if ($Backend -eq 'vulkan' -and $rid -ne 'win-x64') { Write-Host "skip $rid (vulkan は x64 のみ)"; continue }
    $dest = if ($Out) { Join-Path $Out $rid } else { Join-Path $root "out\$rid" }
    $args = @('publish', (Join-Path $root 'src\Whisparrot.App'), '-c', 'Release', '-r', $rid, "-p:Backend=$Backend", '-o', $dest)
    if ($vc) { $args += "-p:VcRedistDir=$vc" }
    dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $rid" }
    $exe = Join-Path $dest 'whisparrot.exe'
    $len = (Get-Item $exe).Length
    Write-Host ("{0}: {1:N0} bytes ({2:N1} MiB)" -f $rid, $len, ($len / 1MB))
}
