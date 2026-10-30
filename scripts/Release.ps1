[CmdletBinding()]
param(
    [Alias('SboxDir')][string]$SboxDirectory,
    [string]$DotnetPath,
    [switch]$SkipBuild,
    [switch]$SkipTests
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$repoRoot = Get-SadeRoot
$enginePath = Get-SadeEngine $SboxDirectory
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') -SboxDirectory $enginePath -DotnetPath $DotnetPath }
$artifactsRoot = Join-Path $repoRoot '.artifacts'
$packageRoot = Join-Path $artifactsRoot 'package\sade'
Assert-SadePackage $packageRoot
if (-not $SkipTests) {
    $dotnetExecutable = Get-SadeDotnet $DotnetPath
    foreach ($project in @('Sade.Formats.Tests', 'Sade.Weapon.Tests', 'Sade.Aiming.Tests', 'Sade.Texture.Tests', 'Sade.Prefab.Tests', 'Sade.Presentation.Tests', 'Sade.Reader.Tests', 'Sade.Vehicle.Tests', 'Sade.Drivetrain.Tests', 'Sade.World.Tests')) {
        $testArguments = @('run', '--project', (Join-Path $repoRoot "tests\$project"), '-c', 'Release')
        if ($project -in @('Sade.Vehicle.Tests', 'Sade.World.Tests') -and $env:SADE_GAME_DIR) { $testArguments += @('--', '--installed') }
        Invoke-SadeDotnet $dotnetExecutable $testArguments
    }
    Invoke-SadeDotnet $dotnetExecutable @('run', '--project', (Join-Path $repoRoot 'tests\Sade.Bootstrap.Tests'), '-c', 'Release', '--', (Join-Path $packageRoot 'sade.dll'), $enginePath)
}
$releaseRoot = Assert-SadeChildPath (Join-Path $artifactsRoot 'release') $artifactsRoot
if (Test-Path -LiteralPath $releaseRoot) { Remove-Item -LiteralPath $releaseRoot -Recurse -Force }
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$releaseMount = Join-Path $releaseRoot 'sade'
Copy-Item -LiteralPath $packageRoot -Destination $releaseMount -Recurse -Force
$setupRoot = Join-Path $releaseMount 'setup'
New-Item -ItemType Directory -Path $setupRoot -Force | Out-Null
foreach ($name in @('Install.ps1', 'Common.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $setupRoot $name) }
$instructions = @'
SADE native mount

1. Close s&box.
2. Extract sade-mount.zip into s&box's mount folder. The result should be
   sbox\mount\sade\sade.dll.
3. Launch s&box and activate GTA San Andreas - The Definitive Edition in Mounts.

To find the s&box folder, right-click it in Steam and choose Manage > Browse
local files. No SDK or build tools are needed to use this release.

In Sandbox, open the spawn menu's Mounts tab and select the GTA mount.
Characters and vehicles activate their behavior automatically when spawned.

To play the world, open Sandbox > New game > Map, choose GTA San Andreas - The
Definitive Edition under Source, then select San Andreas. Map support is
experimental; see the README and release notes for the current limits.

When updating, close s&box and replace the sade folder with the new release.
Keep sade.json if you have customized it.

Optional: setup\Install.ps1 can copy the folder for you from PowerShell.
It finds s&box through FACEPUNCH_ENGINE or the usual Steam location; supply
-SboxDirectory to choose another installation. Supply -Project <project.sbproj>
to enable the mount in that project automatically.

GTA San Andreas - The Definitive Edition must remain installed separately.
Source code and build instructions are in the repository; this release
contains no game data or s&box engine assemblies.
'@
Set-Content -LiteralPath (Join-Path $releaseMount 'SETUP.txt') -Value $instructions -Encoding UTF8
Assert-SadePackage $releaseMount
$archivePath = Assert-SadeChildPath (Join-Path $artifactsRoot 'sade-mount.zip') $artifactsRoot
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
Compress-Archive -LiteralPath $releaseMount -DestinationPath $archivePath -CompressionLevel Optimal
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    foreach ($entry in $archive.Entries) {
        $entryPath = $entry.FullName.Replace('\', '/')
        if (-not $entryPath.StartsWith('sade/', [StringComparison]::Ordinal) -or $entryPath -match '(^|/)(\.cache|\.tools|\.references|obj|bin)/|\.(pak|uasset|uexp|ubulk|ifp|img)$|(^|/)Sandbox\.[^/]*\.dll$|(^|/)oo2core[^/]*\.dll$') {
            throw "Unexpected release entry: $entryPath"
        }
    }
    if (-not ($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq 'sade/setup/Install.ps1' })) { throw 'The release archive is missing its installer.' }
}
finally { $archive.Dispose() }
$checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($archivePath + '.sha256') -Value "$checksum  sade-mount.zip" -Encoding ASCII
Write-Host "Release: $archivePath"
Write-Host "SHA256: $checksum"
