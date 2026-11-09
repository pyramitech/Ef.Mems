# Builds, tests and packs Ef.Mems into .\artifacts (nupkg + snupkg).
#
#   .\pack.ps1                       # Release
#   .\pack.ps1 -SkipTests            # pack only
#
# The single Ef.Mems package contains the runtime library (lib/net10.0) and the
# source generator (analyzers/dotnet/cs); consumers need one PackageReference.
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$Output = 'artifacts',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root $Output

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'Ef.Mems.sln') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit code $LASTEXITCODE)" }
}

dotnet pack (Join-Path $root 'src\Ef.Mems\Ef.Mems.csproj') -c $Configuration -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "Pack failed (exit code $LASTEXITCODE)" }

Write-Host "Packages written to $out"
Get-ChildItem $out -Filter 'Ef.Mems.*nupkg' | ForEach-Object { Write-Host "  $($_.Name)" }
