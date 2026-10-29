[CmdletBinding()]
param([Alias('SboxDir')][string]$SboxDirectory)
. (Join-Path $PSScriptRoot 'Common.ps1')
$repoRoot = Get-SadeRoot
$enginePath = Get-SadeEngine $SboxDirectory
$compiler = Join-Path $enginePath 'bin\managed\ShaderCompiler.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "The installed s&box version is missing ShaderCompiler.exe: $compiler" }
$source = Join-Path $repoRoot 'src\Sade.Mount\Assets\shaders\sade_surface.shader'
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Shader source is missing: $source" }
# ShaderCompiler switches to the engine directory and only compiles sources within its tree.
$mountRoot = Assert-SadeChildPath (Join-Path $enginePath 'mount\sade') (Join-Path $enginePath 'mount')
$compilerSource = Assert-SadeChildPath (Join-Path $mountRoot 'assets\shaders\sade_surface.shader') $mountRoot
New-Item -ItemType Directory -Path (Split-Path -Parent $compilerSource) -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $compilerSource -Force
& $compiler $compilerSource -f -j 4 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "ShaderCompiler failed with exit code $LASTEXITCODE." }
$compiled = $compilerSource + '_c'
if (-not (Test-Path -LiteralPath $compiled -PathType Leaf) -or (Get-Item -LiteralPath $compiled).Length -eq 0 -or (Get-Item -LiteralPath $compiled).LastWriteTimeUtc -lt (Get-Item -LiteralPath $source).LastWriteTimeUtc) {
    throw 'ShaderCompiler did not produce a current, nonempty sade_surface.shader_c.'
}
Copy-Item -LiteralPath $compiled -Destination ($source + '_c') -Force
Write-Host "Compiled shader: $($source + '_c')"
