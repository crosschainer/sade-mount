[CmdletBinding()]
param(
    [Alias('SboxDir')][string]$SboxDirectory,
    [string]$DotnetPath,
    [Alias('Project')][string]$ProjectPath,
    [switch]$SkipBuild
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$repoRoot = Get-SadeRoot
$enginePath = Get-SadeEngine $SboxDirectory
$binaryRelease = Test-Path -LiteralPath (Join-Path $repoRoot 'sade.dll') -PathType Leaf
if (-not $binaryRelease -and -not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') -SboxDirectory $enginePath -DotnetPath $DotnetPath }
$packageRoot = if ($binaryRelease) { $repoRoot } else { Join-Path $repoRoot '.artifacts\package\sade' }
Assert-SadePackage $packageRoot
$installRoot = Assert-SadeChildPath (Join-Path $enginePath 'mount\sade') (Join-Path $enginePath 'mount')
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
try {
    foreach ($file in Get-ChildItem -LiteralPath $packageRoot -File -Recurse) {
        if ($file.Name -like 'Sandbox.*.dll') { throw "Engine DLL must not be distributed in the mount: $($file.Name)" }
        $relative = $file.FullName.Substring($packageRoot.TrimEnd('\', '/').Length).TrimStart('\', '/')
        $destination = Assert-SadeChildPath (Join-Path $installRoot $relative) $installRoot
        if ($file.FullName.Equals($destination, [StringComparison]::OrdinalIgnoreCase)) { continue }
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
}
catch {
    throw "Could not install '$installRoot'. Close s&box if it has the mount DLLs open, then retry. $($_.Exception.Message)"
}
if ($ProjectPath) { Enable-SadeMount -ProjectPath $ProjectPath }
Write-Host "Installed: $installRoot"
Write-Host 'Restart s&box to discover the plugin, then enable GTA San Andreas - The Definitive Edition in Mounts.'
