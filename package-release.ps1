#requires -Version 7
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$InputDirectory = (Join-Path $PSScriptRoot 'artifacts/plugins'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts/release')
)
$ErrorActionPreference = 'Stop'
if ($Tag -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+$') { throw "无效 Release tag：$Tag" }
$packages = @(Get-ChildItem -LiteralPath $InputDirectory -Directory | Sort-Object Name)
if ($packages.Count -eq 0) { throw '没有可发布的插件目录。' }
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 0) {
    throw "Release 输出目录不为空：$OutputDirectory"
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$projects = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Recurse -File -Filter 'Plugins.*.csproj')
$entries = foreach ($package in $packages) {
    $id = $package.Name
    if ($id -cnotmatch '^[a-z0-9][a-z0-9_-]*$') { throw "无效插件 ID：$id" }
    $assemblies = @(Get-ChildItem -LiteralPath $package.FullName -File -Filter '*.dll' |
        Where-Object { $_.BaseName -eq "Plugins.$id" })
    if ($assemblies.Count -ne 1) { throw "找不到唯一入口 DLL：$id" }
    $project = @($projects | Where-Object { $_.BaseName -eq $assemblies[0].BaseName })
    if ($project.Count -ne 1) { throw "找不到唯一插件项目：$id" }
    $projectXml = [xml](Get-Content -LiteralPath $project[0].FullName -Raw)
    $descriptionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/Description')
    $description = if ($null -eq $descriptionNode) { '' } else { [string]$descriptionNode.InnerText }
    if ([string]::IsNullOrWhiteSpace($description)) { throw "插件项目缺少 Description：$id" }
    $asset = "$id.zip"
    $archive = Join-Path $OutputDirectory $asset
    Compress-Archive -LiteralPath $package.FullName -DestinationPath $archive
    $version = [Reflection.AssemblyName]::GetAssemblyName($assemblies[0].FullName).Version.ToString()
    $content = @(Get-ChildItem -LiteralPath $package.FullName -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($package.FullName, $_.FullName).Replace('\', '/')
        "$relative $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
    }) -join "`n"
    [pscustomobject]@{
        id = $id
        name = $assemblies[0].BaseName.Substring('Plugins.'.Length)
        description = $description.Trim()
        runtime = 'dotnet'
        version = $version
        asset = $asset
        sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        contentSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($content))).ToLowerInvariant()
        sizeBytes = (Get-Item -LiteralPath $archive).Length
    }
}
@{ schemaVersion = 1; tag = $Tag; plugins = @($entries) } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release-index.json') -Encoding utf8NoBOM
$notes = @("# $Tag 插件", '')
foreach ($entry in @($entries)) {
    $notes += "## $($entry.name) ($($entry.id))"
    $notes += $entry.description
    $notes += "版本：$($entry.version) · 运行时：$($entry.runtime) · 下载：$($entry.asset)"
    $notes += ''
}
$notes | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release-notes.md') -Encoding utf8NoBOM
