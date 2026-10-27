$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '../extensions/video-game'
$manifest = Get-Content (Join-Path $root 'extension.json') -Raw | ConvertFrom-Json
foreach ($file in $manifest.files.PSObject.Properties) {
    $path = Join-Path $root $file.Name
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.Value) {
        throw "Extension hash mismatch: $($file.Name)"
    }
}
Write-Host 'Bundled extension provenance verified.'
