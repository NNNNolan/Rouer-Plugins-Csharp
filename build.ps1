#requires -Version 7
<#
.SYNOPSIS
将 ForwardAPI 编译到 artifacts/plugins/forwardapi，不会安装或重载宿主。
.PARAMETER Configuration
构建配置，默认为 Release。
.PARAMETER OutputDirectory
发行包父目录，默认 artifacts/plugins；各插件目标目录必须为空，避免旧依赖残留。
.PARAMETER Project
插件项目文件，默认为 ForwardAPI。
.PARAMETER PluginKey
安装目录名，默认为 forwardapi。
#>
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/plugins'),
    [string]$Project = (Join-Path $PSScriptRoot 'src/Plugins.ForwardAPI/Plugins.ForwardAPI.csproj'),
    [ValidatePattern('^[a-z0-9][a-z0-9_-]*$')][string]$PluginKey = 'forwardapi'
)
$ErrorActionPreference = 'Stop'
$projectFile = (Resolve-Path -LiteralPath $Project).Path
$projectName = [IO.Path]::GetFileNameWithoutExtension($projectFile)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$output = Join-Path $outputRoot $PluginKey
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -ne 0) {
    throw "发行目录不为空：$output。请用 -OutputDirectory 指定新的发行目录，不合并旧包。"
}
& dotnet build $projectFile -c $Configuration --disable-build-servers -m:1 `
    -p:ConcurrentBuild=false -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw "插件构建失败：$projectFile" }
$source = Join-Path ([IO.Path]::GetDirectoryName($projectFile)) "bin/$Configuration/net10.0"
New-Item -ItemType Directory -Force -Path $output | Out-Null
# Contracts 由宿主加载；不把它的 DLL/deps.json 当作第二个主入口发布。
foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
    if ($file.Name -like 'Router.Contracts.*') { continue }
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
    $destination = [IO.Path]::GetFullPath((Join-Path $output $relative))
    if (-not $destination.StartsWith([IO.Path]::GetFullPath($output) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw '发行文件路径越界。'
    }
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
$entryDll = Join-Path $output "$projectName.dll"
if (-not (Test-Path -LiteralPath $entryDll)) { throw "未产生入口 DLL：$entryDll" }
Write-Output "构建完成：$output。请按 README 安装到宿主 plugins 目录。"
