<#
.SYNOPSIS
    Builds Story Share and packages it as a Jellyfin plugin zip.

.EXAMPLE
    ./build.ps1
    ./build.ps1 -Version 1.1.0.0
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0.0',
    [string]$TargetAbi = '10.11.0.0',
    [string]$Configuration = 'Release',
    [string]$RepoUrl = 'https://github.com/markdanielaguinaldo/StoryShare',
    [string]$Changelog = '',

    # The catalogue thumbnail, for a server that has not installed the plugin yet and
    # so has no copy of its own. raw.githubusercontent, not github.com — the latter
    # serves an HTML page around the file and Jellyfin gets a broken image.
    [string]$ImageUrl = 'https://raw.githubusercontent.com/markdanielaguinaldo/StoryShare/main/docs/logo.png',

    # Skips rewriting manifest.json, for a build you do not intend to publish.
    [switch]$NoManifest
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'Jellyfin.Plugin.StoryShare\Jellyfin.Plugin.StoryShare.csproj'
$publish = Join-Path $root "artifacts\publish"
$stage = Join-Path $root "artifacts\stage\StoryShare_$Version"
$distDir = Join-Path $root 'artifacts\dist'

foreach ($path in @($publish, $stage, $distDir)) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}

Write-Host "Building $Version..." -ForegroundColor Cyan
dotnet publish $project -c $Configuration -o $publish `
    -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# Every dependency is provided by the server at runtime, so only our own assembly ships.
$shipped = @('Jellyfin.Plugin.StoryShare.dll')
foreach ($name in $shipped) {
    $source = Join-Path $publish $name
    if (-not (Test-Path $source)) { throw "Expected build output missing: $name" }
    Copy-Item $source -Destination $stage
}

# The logo travels inside the package as well as being linked from the manifest:
# once the plugin is installed the dashboard serves the image from the plugin's own
# folder, and a server with no route to raw.githubusercontent would otherwise show
# an empty tile for something it already has on disk.
$logo = Join-Path $root 'docs\logo.png'
if (-not (Test-Path $logo)) { throw "Expected logo missing: $logo" }
Copy-Item $logo -Destination (Join-Path $stage 'logo.png')

$meta = [ordered]@{
    guid        = 'b6e3a1c4-5d27-4f8a-9c31-7a0f2d84e5b9'
    name        = 'Story Share'
    description = 'Share movies, shows and music from your library as an Instagram Story card.'
    overview    = 'Renders a 1080x1920 story card from any library item in nine styles, as a still image or a looping video, and hands it to your phone.'
    owner       = 'markdanielaguinaldo'
    category    = 'General'
    version     = $Version
    targetAbi   = $TargetAbi
    framework   = 'net9.0'
    # Relative to the plugin's own folder, which is what the server serves it from.
    imagePath   = 'logo.png'
    timestamp   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}
# Written with an explicit BOM-less encoder, NOT Out-File -Encoding utf8: on Windows
# PowerShell 5.1 that emits a UTF-8 BOM, and Jellyfin reads meta.json as a string
# before deserializing, so the BOM lands inside the JSON and the parse fails. The
# server recovers by regenerating its own manifest, but it logs an error on every
# install and the plugin loses its description, overview, owner and category.
[System.IO.File]::WriteAllText(
    (Join-Path $stage 'meta.json'),
    ($meta | ConvertTo-Json -Depth 3),
    (New-Object System.Text.UTF8Encoding $false))

$zipPath = Join-Path $distDir "storyshare_$Version.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -Force

$checksum = (Get-FileHash $zipPath -Algorithm MD5).Hash.ToLower()

# ---------------------------------------------------------------- repository manifest
#
# Jellyfin verifies the manifest's checksum against the downloaded zip, so a
# hand-copied hash that drifts from the artifact breaks installs with a confusing
# error. Rewriting the entry here means the two can never disagree.
if (-not $NoManifest) {
    $manifestPath = Join-Path $root 'manifest.json'

    $entry = [ordered]@{
        version   = $Version
        changelog = $Changelog
        targetAbi = $TargetAbi
        sourceUrl = "$($RepoUrl.TrimEnd('/'))/releases/download/v$Version/storyshare_$Version.zip"
        checksum  = $checksum
        timestamp = $meta.timestamp
    }

    if (Test-Path $manifestPath) {
        # Do NOT wrap this in @(): Windows PowerShell 5.1's ConvertFrom-Json emits a
        # JSON array as one object, so @() nests it and property assignment below
        # fails with "the property 'versions' cannot be found on this object".
        $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $plugin = if ($manifest -is [array]) { $manifest[0] } else { $manifest }

        # Replace this version if it is already listed, otherwise put it on top:
        # Jellyfin offers whichever entry it finds first for a given version.
        $others = @($plugin.versions | Where-Object { $_.version -ne $Version })
        $plugin.versions = @([pscustomobject]$entry) + $others

        # -Force because a manifest written before the plugin had a logo has no such
        # property, and plain assignment onto a PSCustomObject cannot create one.
        $plugin | Add-Member -NotePropertyName imageUrl -NotePropertyValue $ImageUrl -Force
    }
    else {
        $plugin = [pscustomobject][ordered]@{
            guid        = $meta.guid
            name        = $meta.name
            description = $meta.description
            overview    = $meta.overview
            owner       = $meta.owner
            category    = $meta.category
            imageUrl    = $ImageUrl
            versions    = @([pscustomobject]$entry)
        }
    }

    # BOM-less again — raw.githubusercontent serves the bytes verbatim and
    # Jellyfin's JSON reader chokes on a leading BOM exactly as it does for meta.json.
    # @() keeps the top level an array even with a single plugin in it.
    [System.IO.File]::WriteAllText(
        $manifestPath,
        (ConvertTo-Json @($plugin) -Depth 5),
        (New-Object System.Text.UTF8Encoding $false))

    Write-Host "Manifest: $manifestPath updated for $Version"
}

Write-Host ''
Write-Host "Package:  $zipPath" -ForegroundColor Green
Write-Host "Checksum: $checksum (MD5)"
Write-Host ''
Write-Host 'To publish this version:'
Write-Host "  1. gh release create v$Version `"$zipPath`"   (or upload it in the GitHub UI)"
Write-Host '  2. commit and push the updated manifest.json'
Write-Host ''
Write-Host 'To install manually instead, unzip into your Jellyfin plugins folder:'
Write-Host '  Linux/Docker : /config/plugins/StoryShare/'
Write-Host '  Windows      : %ProgramData%\Jellyfin\Server\plugins\StoryShare\'
Write-Host 'then restart Jellyfin.'
