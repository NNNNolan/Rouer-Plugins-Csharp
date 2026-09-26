#requires -Version 7
<#
.SYNOPSIS
运行提供方与宿主的集成回归；普通插件编译不需要检出宿主仓库。
.PARAMETER RouterHostRoot
Router2API 检出目录。默认采用同级目录，可显式指定其他位置。
#>
param([string]$RouterHostRoot = (Join-Path $PSScriptRoot '../Router2API'))
$ErrorActionPreference = 'Stop'
$hostRoot = (Resolve-Path -LiteralPath $RouterHostRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $hostRoot 'src/Router.Host/Router.Host.csproj'))) {
    throw 'RouterHostRoot 必须指向 Router2API 仓库根目录。'
}
# RouterHostRoot 是全局构建属性，让插件和宿主测试共用同一份 Contracts 类型身份。
& dotnet test (Join-Path $PSScriptRoot 'tests/Router.Tests/Router.Tests.csproj') `
    "-p:RouterHostRoot=$hostRoot" --disable-build-servers -m:1 `
    -p:ConcurrentBuild=false -p:UseSharedCompilation=false --logger 'console;verbosity=minimal'
exit $LASTEXITCODE
