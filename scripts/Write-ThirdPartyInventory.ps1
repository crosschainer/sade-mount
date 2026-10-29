[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)
. (Join-Path $PSScriptRoot 'Common.ps1')
$repoRoot = Get-SadeRoot
$assetsFile = Join-Path $repoRoot 'src\Sade.Reader\obj\project.assets.json'
if (-not (Test-Path -LiteralPath $assetsFile -PathType Leaf)) { throw 'Restore/build Sade.Reader before generating dependency notices.' }
$assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json
$noticeRoot = [System.IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null
$supplementalRoot = Join-Path $repoRoot 'ThirdParty'
$supplementalManifestPath = Join-Path $supplementalRoot 'package-licenses.json'
$supplementalLicenses = @(Get-Content -LiteralPath $supplementalManifestPath -Raw | ConvertFrom-Json)
$inventory = @()
foreach ($entry in $assets.libraries.PSObject.Properties | Sort-Object Name) {
    if ($entry.Value.type -ne 'package') { continue }
    $packageDirectory = $null
    foreach ($packageFolder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $packageFolder $entry.Value.path
        if (Test-Path -LiteralPath $candidate -PathType Container) { $packageDirectory = $candidate; break }
    }
    if (-not $packageDirectory) { throw "Restored dependency was not found: $($entry.Name)" }
    $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File | Select-Object -First 1
    if (-not $nuspec) { throw "No NuGet metadata for $($entry.Name)" }
    [xml]$document = Get-Content -LiteralPath $nuspec.FullName -Raw
    $metadata = $document.SelectSingleNode("//*[local-name()='metadata']")
    $license = $metadata.SelectSingleNode("*[local-name()='license']")
    $licenseUrl = $metadata.SelectSingleNode("*[local-name()='licenseUrl']")
    $repository = $metadata.SelectSingleNode("*[local-name()='repository']")
    $projectUrl = $metadata.SelectSingleNode("*[local-name()='projectUrl']")
    $supplement = @($supplementalLicenses | Where-Object { $_.Packages -contains $entry.Name })
    $inventory += [ordered]@{
        Package = $entry.Name
        License = if ($license) { $license.InnerText } else { $null }
        LicenseType = if ($license) { $license.GetAttribute('type') } else { $null }
        LicenseUrl = if ($licenseUrl) { $licenseUrl.InnerText } else { $null }
        Repository = if ($repository) { $repository.GetAttribute('url') } else { $null }
        ProjectUrl = if ($projectUrl) { $projectUrl.InnerText } else { $null }
        SupplementalLicenseFiles = @($supplement | ForEach-Object { $_.File })
        SupplementalLicenseSources = @($supplement | ForEach-Object { $_.SourceUrl })
    }
    $packageNotices = Assert-SadeChildPath (Join-Path $noticeRoot $entry.Name.Replace('/', '_')) $noticeRoot
    New-Item -ItemType Directory -Path $packageNotices -Force | Out-Null
    Copy-Item -LiteralPath $nuspec.FullName -Destination (Join-Path $packageNotices $nuspec.Name) -Force
    foreach ($item in $supplement) {
        $source = Assert-SadeChildPath (Join-Path $supplementalRoot $item.File) $supplementalRoot
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Supplemental license missing for $($entry.Name): $($item.File)" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $packageNotices $item.File) -Force
    }
    foreach ($file in Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Where-Object { $_.Name -match '^(LICENSE|LICENCE|NOTICE|COPYING)(\.|-|$)' }) {
        $relative = $file.FullName.Substring($packageDirectory.TrimEnd('\', '/').Length).TrimStart('\', '/')
        $target = Assert-SadeChildPath (Join-Path $packageNotices $relative) $packageNotices
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}
Copy-Item -LiteralPath $supplementalManifestPath -Destination (Join-Path $noticeRoot 'package-license-origins.json') -Force
$inventory | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $noticeRoot 'packages.json') -Encoding UTF8
Write-Host "Recorded $($inventory.Count) dependency licenses in $noticeRoot"
