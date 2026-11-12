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

.PARAMETER MaxVersions
    How many versions to keep in the manifest. Older versions let servers with an
    older targetAbi find a compatible build. Defaults to 20; 0 means unlimited.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Checksum,
    [Parameter(Mandatory = $true)][string]$SourceUrl,
    [string]$Changelog,
    [string]$Repository = 'Louis-15/Jellyfin-AV-Collection',
    [int]$MaxVersions = 20
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSCommandPath
$metaPath = Join-Path $root '..\meta.json'
$manifestPath = [System.IO.Path]::GetFullPath((Join-Path $root '..\manifest.json'))

# meta.json 是纯对象，PowerShell 5.1 与 7 解析结果一致（数组才会有歧义）。
$meta = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($metaPath, [System.Text.Encoding]::UTF8))

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

# ---- 读取旧清单里的历史版本 -------------------------------------------------
# 保留历史版本是为了让 targetAbi 较旧的 Jellyfin 仍能在清单里找到兼容版本，
# 而不是只能看到最新版。PowerShell 5.1 会把单元素数组退化成对象，统一用 @() 包一层。
$versions = @()
if (Test-Path $manifestPath) {
    $raw = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8)
    if ($raw.Trim().Length -gt 0) {
        $existing = ConvertFrom-Json -InputObject $raw
        $plugin = @($existing) | Select-Object -First 1
        if ($null -ne $plugin -and $null -ne $plugin.versions) {
            $versions = @($plugin.versions)
        }
    }
}

# 同版本重跑时覆盖旧条目，避免清单里出现两条相同版本号
$versions = @($versions | Where-Object { $_.version -ne $Version })

# 历史版本按语义版本从高到低排；解析失败的条目垫底，不打断发布
$versions = @($versions | Sort-Object -Property @{
    Expression = { try { [version]$_.version } catch { [version]'0.0.0.0' } }
} -Descending)

$allVersions = @($entry) + $versions
if ($MaxVersions -gt 0 -and $allVersions.Count -gt $MaxVersions) {
    $allVersions = @($allVersions[0..($MaxVersions - 1)])
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
        versions    = @($allVersions)
    }
)

$json = ConvertTo-Json -InputObject $manifest -Depth 8
[System.IO.File]::WriteAllText($manifestPath, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host "manifest.json -> $Version ($($entry.checksum), $($allVersions.Count) versions)"
