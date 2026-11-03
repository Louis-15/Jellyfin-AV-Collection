#requires -Version 5.1
<#
.SYNOPSIS
    Cuts a new release: bumps the version, commits, tags and pushes.

.DESCRIPTION
    The tag push starts the GitHub workflow, which tests, packages, writes the checksum
    into manifest.json, commits it back and publishes the release asset. That keeps the
    zip Jellyfin downloads and the checksum in the manifest identical.

.EXAMPLE
    .\release.ps1 -Version 1.0.1.0 -Changelog "修复 xxx"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Changelog
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)

if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "版本号格式应为 1.2.3 或 1.2.3.4，收到：$Version"
}

if (-not $Changelog) {
    $Changelog = "Release $Version"
}

Write-Host "==> 更新版本号到 $Version" -ForegroundColor Cyan

# csproj
$csproj = Join-Path $root 'Jellyfin.Plugin.AVCollection\Jellyfin.Plugin.AVCollection.csproj'
$text = [System.IO.File]::ReadAllText($csproj)
$text = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
$text = [regex]::Replace($text, '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version</AssemblyVersion>")
$text = [regex]::Replace($text, '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$Version</FileVersion>")
[System.IO.File]::WriteAllText($csproj, $text, $utf8)

# meta.json（纯对象，PowerShell 5.1 / 7 行为一致）
$metaPath = Join-Path $root 'meta.json'
$meta = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($metaPath))
$meta.version = $Version
[System.IO.File]::WriteAllText($metaPath, (ConvertTo-Json -InputObject $meta -Depth 5) + "`n", $utf8)

# manifest.json 由 workflow 用实际打包产物的 checksum 重新生成，这里不动。

Write-Host "==> 提交并打标签 v$Version" -ForegroundColor Cyan
# git 会往 stderr 写进度和 CRLF 警告，在 ErrorActionPreference=Stop 下会被当成异常中断脚本。
$ErrorActionPreference = 'Continue'
git -C $root add -A
git -C $root commit -m "Release $Version" -m $Changelog
if ($LASTEXITCODE -ne 0) { throw 'git commit 失败（是否没有改动？）' }

# CI 每次发版后会把新 checksum 回写进 manifest.json 并提交回 main，
# 所以本地 main 总是落后一个提交，直接 push 必然被拒（non-fast-forward）。
# 先追平远端再打 tag —— tag 必须打在 rebase 之后的提交上，
# 否则 tag 指向被丢弃的旧提交，Release 里的 zip 会和 main 对不上。
git -C $root fetch origin main
git -C $root rebase origin/main
if ($LASTEXITCODE -ne 0) {
    throw 'git rebase origin/main 失败：先手动解决冲突，再执行 git tag / git push'
}

# annotated tag：workflow 会把 tag 消息当作插件目录里的更新说明。
git -C $root tag -a "v$Version" -m $Changelog
git -C $root push origin main
if ($LASTEXITCODE -ne 0) { throw 'git push main 失败' }
git -C $root push origin "v$Version"
if ($LASTEXITCODE -ne 0) { throw 'git push tag 失败' }

Write-Host ''
Write-Host "已推送 v$Version。GitHub Actions 会完成编译、打包、写入 checksum 并发布 Release：" -ForegroundColor Green
Write-Host "  https://github.com/Louis-15/Jellyfin-AV-Collection/actions"
Write-Host "  https://github.com/Louis-15/Jellyfin-AV-Collection/releases"
