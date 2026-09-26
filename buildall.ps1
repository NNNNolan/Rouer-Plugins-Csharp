#requires -Version 7
<#
.SYNOPSIS
扫描 src/Plugins.* 项目，逐个生成独立发行目录，不安装或重载宿主。
#>
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/plugins')
)
$ErrorActionPreference = 'Stop'
$projects = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Recurse -File -Filter 'Plugins.*.csproj' |
    Sort-Object FullName)
if ($projects.Count -eq 0) { throw 'src 中没有找到 Plugins.*.csproj 插件项目。' }

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$plugins = @($projects | ForEach-Object {
    [pscustomobject]@{
        Project = $_.FullName
        Key = $_.BaseName.Substring('Plugins.'.Length).ToLowerInvariant()
    }
})
foreach ($plugin in $plugins) {
    if ($plugin.Key -notmatch '^[a-z0-9][a-z0-9_-]*$') { throw "无效插件名：$($plugin.Project)" }
    $target = Join-Path $outputRoot $plugin.Key
    if ((Test-Path -LiteralPath $target) -and @(Get-ChildItem -LiteralPath $target -Force).Count -ne 0) {
        throw "发行目录不为空：$target。请用 -OutputDirectory 指定新的发行目录，不合并旧包。"
    }
}
if (@($plugins | Group-Object Key | Where-Object Count -gt 1).Count -ne 0) { throw '插件安装目录名重复。' }

foreach ($plugin in $plugins) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Project $plugin.Project -PluginKey $plugin.Key `
        -Configuration $Configuration -OutputDirectory $outputRoot
}
