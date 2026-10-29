Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SadeRoot {
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}

function Assert-SadeChildPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Parent)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path must remain below '$fullParent': $fullPath"
    }
    return $fullPath
}

function Get-SadeEngine {
    param([string]$Requested)
    $candidates = @()
    if ($Requested) { $candidates = @($Requested) }
    else {
        $candidates = @($env:FACEPUNCH_ENGINE, [Environment]::GetEnvironmentVariable('FACEPUNCH_ENGINE', 'User'), 'C:\Program Files (x86)\Steam\steamapps\common\sbox')
    }
    foreach ($candidate in $candidates) {
        if (-not $candidate) { continue }
        $resolved = [System.IO.Path]::GetFullPath($candidate)
        if (Test-Path -LiteralPath (Join-Path $resolved 'bin\managed\Sandbox.Engine.dll') -PathType Leaf) {
            return $resolved
        }
    }
    throw 's&box was not found. Supply -SboxDirectory or set FACEPUNCH_ENGINE.'
}

function Get-SadeDotnet {
    param([string]$Requested)
    $candidates = @()
    if ($Requested) { $candidates = @($Requested) }
    else {
        $command = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($command) { $candidates += $command.Source }
        $candidates += Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'sade-mount\dotnet\dotnet.exe'
        $candidates += Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet\dotnet.exe'
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $sdks = @(& $candidate --list-sdks)
        if ($LASTEXITCODE -eq 0 -and ($sdks | Where-Object { $_ -match '^10\.' })) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }
    throw 'The .NET 10 SDK is required. Install it or supply -DotnetPath to a dotnet.exe with a 10.x SDK.'
}

function Invoke-SadeDotnet {
    param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string[]]$Arguments)
    & $Executable @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE." }
}

function Get-SadeProject {
    param([Parameter(Mandatory)][string]$Path)
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if (Test-Path -LiteralPath $resolved -PathType Container) {
        $files = @(Get-ChildItem -LiteralPath $resolved -File -Force -Filter '*.sbproj')
        if ($files.Count -ne 1) { throw "Expected exactly one .sbproj in '$resolved'. Supply the .sbproj path explicitly." }
        return $files[0].FullName
    }
    if ([System.IO.Path]::GetExtension($resolved) -ne '.sbproj' -and [System.IO.Path]::GetFileName($resolved) -ne '.sbproj') {
        throw "Not an s&box project: $resolved"
    }
    return $resolved
}

function Enable-SadeMount {
    param([Parameter(Mandatory)][string]$ProjectPath)
    $projectFile = Get-SadeProject $ProjectPath
    $config = Get-Content -LiteralPath $projectFile -Raw | ConvertFrom-Json
    $mounts = if ($config.PSObject.Properties['Mounts']) { @($config.Mounts | Where-Object { $_ }) } else { @() }
    if ($mounts -contains 'sade') { Write-Host "SADE is enabled: $projectFile"; return }
    $mounts += 'sade'
    $config | Add-Member -MemberType NoteProperty -Name Mounts -Value @($mounts) -Force
    $config | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $projectFile -Encoding UTF8
    Write-Host "Enabled SADE: $projectFile"
}

function Assert-SadePackage {
    param([Parameter(Mandatory)][string]$Path)
    foreach ($relative in @('sade.dll', 'Sade.Contracts.dll', 'backend\Sade.Reader.dll', 'backend\Sade.Reader.deps.json', 'assets\shaders\sade_surface.shader_c', 'third-party\CUE4Parse-vendored-animation\LICENSE', 'third-party\CUE4Parse-vendored-animation\NOTICE', 'third-party\CUE4Parse-vendored-animation\ORIGIN.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $relative) -PathType Leaf)) { throw "Mount package is incomplete: $relative. Build the source or download a complete release." }
    }
    foreach ($file in Get-ChildItem -LiteralPath $Path -Recurse -File) {
        if ($file.Name -like 'Sandbox.*.dll' -or $file.Name -like 'oo2core*.dll' -or $file.Extension -in @('.pak', '.uasset', '.uexp', '.ubulk', '.ifp', '.img')) {
            throw "The mount package contains an engine or game file that must not be distributed: $($file.Name)"
        }
    }
}
