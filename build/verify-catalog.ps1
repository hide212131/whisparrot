<#
.SYNOPSIS
  Re-checks src\Whisparrot.Infrastructure\Models\model-catalog.json against Hugging Face's public metadata
  (no model download): the tree API (lfs.oid = SHA-256, size) at the pinned revision, and the x-linked-etag /
  x-linked-size headers of each resolve URL.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$root = Split-Path -Parent $PSScriptRoot
$catalog = Get-Content (Join-Path $root 'src\Whisparrot.Infrastructure\Models\model-catalog.json') -Raw | ConvertFrom-Json
$tree = Invoke-RestMethod -Uri "https://huggingface.co/api/models/ggerganov/whisper.cpp/tree/$($catalog.revision)" -UseBasicParsing
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$http = New-Object System.Net.Http.HttpClient($handler)
$failed = $false
foreach ($m in $catalog.models) {
    $e = $tree | Where-Object { $_.path -eq $m.filename }
    $apiOk = $e -and $e.lfs.oid -eq $m.sha256 -and [int64]$e.lfs.size -eq [int64]$m.size_bytes
    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Head, $m.url)
    $resp = $http.SendAsync($req).GetAwaiter().GetResult()
    $etag = ($resp.Headers.GetValues('x-linked-etag') | Select-Object -First 1) -replace '"', ''
    $size = [int64]($resp.Headers.GetValues('x-linked-size') | Select-Object -First 1)
    $commit = ($resp.Headers.GetValues('x-repo-commit') | Select-Object -First 1)
    $headOk = $etag -eq $m.sha256 -and $size -eq [int64]$m.size_bytes -and $commit -eq $catalog.revision
    "{0,-16} tree-api={1} resolve-headers={2} size={3} sha256={4}" -f $m.id, $(if ($apiOk) { 'MATCH' } else { 'MISMATCH' }), $(if ($headOk) { 'MATCH' } else { 'MISMATCH' }), $m.size_bytes, $m.sha256
    if (-not ($apiOk -and $headOk)) { $failed = $true }
}
if ($failed) { 'catalog_verify=FAIL'; exit 1 } else { 'catalog_verify=PASS' }
