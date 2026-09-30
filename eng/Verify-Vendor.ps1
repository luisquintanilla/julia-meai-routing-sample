param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$manifest = Get-Content -Raw (Join-Path $root 'vendor\provenance.json') | ConvertFrom-Json
$expected = @()
foreach ($file in $manifest.files) {
    $relative = $file.path.Replace('/', '\')
    $expected += $relative
    $path = Join-Path (Join-Path $root 'vendor') $relative
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $file.sha256) { throw "Vendored file differs from pinned source: $relative" }
}
$actualFiles = Get-ChildItem (Join-Path $root 'vendor\src') -Recurse -File |
    Where-Object { $_.Extension -in '.cs', '.csproj' -and $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object { $_.FullName.Substring((Join-Path $root 'vendor').Length + 1) }
if (Compare-Object $expected $actualFiles) { throw 'Vendored dependency closure differs from the manifest.' }
Write-Output "$($expected.Count) unchanged source/project hashes match $($manifest.commit)."
