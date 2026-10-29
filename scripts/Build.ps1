[CmdletBinding()]
param(
    [Alias('SboxDir')][string]$SboxDirectory,
    [string]$DotnetPath,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$repoRoot = Get-SadeRoot
$enginePath = Get-SadeEngine $SboxDirectory
$dotnetExecutable = Get-SadeDotnet $DotnetPath
foreach ($assembly in @('Sandbox.System.dll', 'Sandbox.Engine.dll', 'Sandbox.Filesystem.dll', 'Sandbox.Reflection.dll', 'Sandbox.Mounting.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $enginePath "bin\managed\$assembly") -PathType Leaf)) { throw "The installed s&box version is missing $assembly. Verify the s&box installation before building." }
}
$shaderSource = Join-Path $repoRoot 'src\Sade.Mount\Assets\shaders\sade_surface.shader'
$shaderCompiled = $shaderSource + '_c'
if (-not (Test-Path -LiteralPath $shaderSource -PathType Leaf)) { throw 'The SADE surface shader source is missing.' }
if (-not (Test-Path -LiteralPath $shaderCompiled -PathType Leaf) -or (Get-Item -LiteralPath $shaderCompiled).LastWriteTimeUtc -lt (Get-Item -LiteralPath $shaderSource).LastWriteTimeUtc) {
    & (Join-Path $PSScriptRoot 'Compile-Shaders.ps1') -SboxDirectory $enginePath
}
$artifactsRoot = Join-Path $repoRoot '.artifacts'
$stagingRoot = Join-Path $artifactsRoot 'publish'
$packageRoot = Assert-SadeChildPath (Join-Path $artifactsRoot 'package\sade') $artifactsRoot

foreach ($outputPath in @($stagingRoot, $packageRoot)) {
    $checkedPath = Assert-SadeChildPath $outputPath $artifactsRoot
    if (Test-Path -LiteralPath $checkedPath) { Remove-Item -LiteralPath $checkedPath -Recurse -Force }
    New-Item -ItemType Directory -Path $checkedPath -Force | Out-Null
}

$mountOutput = Join-Path $stagingRoot 'mount'
$readerOutput = Join-Path $stagingRoot 'reader'
Invoke-SadeDotnet $dotnetExecutable @('publish', (Join-Path $repoRoot 'src\Sade.Mount\Sade.Mount.csproj'), '-c', $Configuration, '--nologo', '-o', $mountOutput, "-p:SboxDirectory=$enginePath")
Invoke-SadeDotnet $dotnetExecutable @('publish', (Join-Path $repoRoot 'src\Sade.Reader\Sade.Reader.csproj'), '-c', $Configuration, '--nologo', '-o', $readerOutput, '-p:EnableDynamicLoading=true')

foreach ($name in @('sade.dll', 'Sade.Contracts.dll')) {
    $source = Join-Path $mountOutput $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Mount output is missing $name." }
    Copy-Item -LiteralPath $source -Destination (Join-Path $packageRoot $name)
}
foreach ($name in @('sade.pdb', 'sade.deps.json', 'Sade.Contracts.pdb')) {
    $source = Join-Path $mountOutput $name
    if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination (Join-Path $packageRoot $name) }
}
$backendOutput = Join-Path $packageRoot 'backend'
New-Item -ItemType Directory -Path $backendOutput -Force | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $readerOutput -File -Recurse) {
    if ($file.Name -like 'Sandbox.*' -or $file.Name -like 'Sade.Contracts.*') { continue }
    $relative = $file.FullName.Substring($readerOutput.TrimEnd('\', '/').Length).TrimStart('\', '/')
    $destination = Assert-SadeChildPath (Join-Path $backendOutput $relative) $backendOutput
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
foreach ($name in @('Sade.Reader.dll', 'Sade.Reader.deps.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $backendOutput $name) -PathType Leaf)) { throw "Reader output is missing $name." }
}
$assetSource = Join-Path $repoRoot 'src\Sade.Mount\Assets'
if (Test-Path -LiteralPath $assetSource -PathType Container) {
    Copy-Item -LiteralPath $assetSource -Destination (Join-Path $packageRoot 'assets') -Recurse -Force
}
if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'assets\shaders\sade_surface.shader_c') -PathType Leaf)) {
    throw 'The compiled SADE surface shader is missing. Compile sade_surface.shader with the installed s&box ShaderCompiler before packaging.'
}
foreach ($name in @('README.md', 'THIRD-PARTY-NOTICES.md')) {
    $source = Join-Path $repoRoot $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $packageRoot $name) }
}
$documentationSource = Join-Path $repoRoot 'docs'
if (Test-Path -LiteralPath $documentationSource -PathType Container) {
    # Showcase recordings are linked from GitHub; they are not needed to install the mount.
    foreach ($file in Get-ChildItem -LiteralPath $documentationSource -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($documentationSource, $file.FullName)
        if ($relative.Replace('\', '/').StartsWith('videos/', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $destination = Assert-SadeChildPath (Join-Path $packageRoot "docs\$relative") $packageRoot
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
}
& (Join-Path $PSScriptRoot 'Write-ThirdPartyInventory.ps1') -Destination (Join-Path $packageRoot 'third-party')
$converterNotices = Join-Path $packageRoot 'third-party\CUE4Parse-vendored-animation'
New-Item -ItemType Directory -Path $converterNotices -Force | Out-Null
foreach ($name in @('LICENSE', 'NOTICE', 'ORIGIN.md')) {
    $source = Join-Path $repoRoot "src\Sade.Reader\ThirdParty\CUE4Parse\$name"
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Vendored animation source is missing $name." }
    Copy-Item -LiteralPath $source -Destination (Join-Path $converterNotices $name)
}
$licenseSource = Join-Path $repoRoot 'ThirdParty'
if (Test-Path -LiteralPath $licenseSource -PathType Container) {
    foreach ($file in Get-ChildItem -LiteralPath $licenseSource -File) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $packageRoot 'third-party') -Force
    }
}
Assert-SadePackage $packageRoot
Write-Host "Built mount package: $packageRoot"
