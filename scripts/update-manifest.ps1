#requires -Version 5.1
<#
.SYNOPSIS
    Regenerates manifest.json for a release.

.DESCRIPTION
    Jellyfin validates a plugin download against the checksum in the repository manifest,
    so the published zip and the manifest must come from the same build. The release
    workflow calls this after packaging.

    The file is written from meta.json plus the arguments instead of being patched in
    place: Windows PowerShell 5.1 and PowerShell 7 disagree on how ConvertFrom-Json
    flattens a single-element array, and getting that wrong silently turns the plugin
    list into an object Jellyfin cannot read.

.PARAMETER Version
    The plugin version, e.g. 1.0.0.1.

.PARAMETER Checksum
    MD5 of the release zip (lowercase hex).

.PARAMETER SourceUrl
    Download URL of the release zip.

.PARAMETER Changelog
    Text shown in the Jellyfin plugin catalog. Defaults to a link to the release page.

.PARAMETER Repository
    GitHub repository (owner/name) used for the logo URL.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Checksum,
    [Parameter(Mandatory = $true)][string]$SourceUrl,
    [string]$Changelog,
    [string]$Repository = 'Louis-15/Jellyfin-AV-Collection'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSCommandPath
$metaPath = Join-Path $root '..\meta.json'
$manifestPath = [System.IO.Path]::GetFullPath((Join-Path $root '..\manifest.json'))

# meta.json 是纯对象，PowerShell 5.1 与 7 解析结果一致（数组才会有歧义）。
$meta = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($metaPath))

if (-not $Changelog) {
    $Changelog = "See https://github.com/$Repository/releases/tag/v$Version"
}

$entry = [pscustomobject]@{
    version   = $Version
    changelog = $Changelog.Trim()
    targetAbi = $meta.targetAbi
    sourceUrl = $SourceUrl
    checksum  = $Checksum.ToLowerInvariant()
    timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}

$manifest = @(
    [pscustomobject]@{
        guid        = $meta.guid
        name        = $meta.name
        description = $meta.description
        overview    = $meta.overview
        owner       = $meta.owner
        category    = $meta.category
        imageUrl    = "https://raw.githubusercontent.com/$Repository/main/logo.png"
        versions    = @($entry)
    }
)

$json = ConvertTo-Json -InputObject $manifest -Depth 6
[System.IO.File]::WriteAllText($manifestPath, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host "manifest.json -> $Version ($($entry.checksum))"
