#requires -Version 7
<#
.SYNOPSIS
运行 C# 插件自身的单元测试；仅使用本仓库的 Contracts 快照，无需检出宿主。
#>
$ErrorActionPreference = 'Stop'
& dotnet test (Join-Path $PSScriptRoot 'tests/Router.Tests/Router.Tests.csproj') `
    --disable-build-servers -m:1 `
    -p:ConcurrentBuild=false -p:UseSharedCompilation=false --logger 'console;verbosity=minimal'
exit $LASTEXITCODE
