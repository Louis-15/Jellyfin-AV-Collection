#requires -Version 5.1
<#
.SYNOPSIS
    Builds the plugin and packs the Jellyfin plugin zip.

.DESCRIPTION
    Produces artifacts/Jellyfin.Plugin.AVCollection_<version>/ (dll + meta.json + logo.png)
    and artifacts/av-collection_<version>.zip, which is what
    /config/plugins/<name>_<version>/ expects after extraction.

.PARAMETER Configuration
    Build configuration, Release by default.

.PARAMETER Dotnet
    Path to the dotnet executable. Defaults to "dotnet" on PATH, then %DOTNET_ROOT%\dotnet.exe.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Dotnet
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $Dotnet) {
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $Dotnet = 'dotnet'
    } elseif ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
        $Dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    } else {
        throw '找不到 dotnet。请安装 .NET 10 SDK，或用 -Dotnet 指定路径。'
    }
}

$project = Join-Path $root 'Jellyfin.Plugin.AVCollection\Jellyfin.Plugin.AVCollection.csproj'
# 显式按 UTF-8 读取：Windows PowerShell 5.1 默认会用 ANSI 代码页，中文简介会变乱码。
$meta = [System.IO.File]::ReadAllText((Join-Path $root 'meta.json')) | ConvertFrom-Json
$version = $meta.version
$artifacts = Join-Path $root 'artifacts'
$packageDir = Join-Path $artifacts "Jellyfin.Plugin.AVCollection_$version"
$zipPath = Join-Path $artifacts "av-collection_$version.zip"

Write-Host "==> 编译 $version ($Configuration)" -ForegroundColor Cyan
& $Dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "编译失败（exit $LASTEXITCODE）"
}

$buildOutput = Join-Path $root "Jellyfin.Plugin.AVCollection\bin\$Configuration\net10.0"

if (Test-Path $packageDir) {
    Remove-Item $packageDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

Copy-Item (Join-Path $buildOutput 'Jellyfin.Plugin.AVCollection.dll') $packageDir
Copy-Item (Join-Path $root 'meta.json') $packageDir
Copy-Item (Join-Path $root 'logo.png') $packageDir

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash $zipPath -Algorithm MD5).Hash.ToLowerInvariant()
Write-Host ''
Write-Host "插件目录: $packageDir"
Write-Host "插件压缩包: $zipPath"
Write-Host "MD5: $hash"
Write-Host ''
Write-Host '安装到服务器：'
Write-Host "  scp -P 2222 `"$zipPath`" root@931002.xyz:/tmp/"
Write-Host "  ssh -p 2222 root@931002.xyz `"mkdir -p /www/jellyfin/config/plugins/AV Collection_$version && unzip -o /tmp/$(Split-Path $zipPath -Leaf) -d '/www/jellyfin/config/plugins/AV Collection_$version' && docker restart jellyfin`""
