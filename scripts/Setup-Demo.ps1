[CmdletBinding()]
param(
    [Alias('SboxDir')][string]$SboxDirectory,
    [string]$DotnetPath,
    [switch]$SkipBuild
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$repoRoot = Get-SadeRoot
$enginePath = Get-SadeEngine $SboxDirectory
$demoParent = Join-Path $repoRoot 'Demo'
$demoRoot = Join-Path $demoParent 'sade_preview'
$cli = Join-Path $enginePath 'bin\managed\sbox-cli.exe'
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw "sbox-cli was not found: $cli" }
if (-not (Test-Path -LiteralPath $demoRoot -PathType Container)) {
    New-Item -ItemType Directory -Path $demoParent -Force | Out-Null
    & $cli --new 'Sade Preview' --template game.minimal --in $demoParent | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sbox-cli project creation failed ($LASTEXITCODE)." }
}
$demoProject = Get-SadeProject $demoRoot
& (Join-Path $PSScriptRoot 'Install.ps1') -SboxDirectory $enginePath -DotnetPath $DotnetPath -ProjectPath $demoProject -SkipBuild:$SkipBuild
Write-Host "Demo project prepared without launching the editor: $demoProject"
Write-Host 'SADE is enabled in this project. Open it in s&box and drag a mounted pedestrian or vehicle prefab into a scene.'
