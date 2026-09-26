# C# 插件 SDK：人和 AI 的开发入口

目标框架为 .NET 10，公共契约为 Contracts 2.0。原生插件实现 `IPlatformTerminal`，从 `host.Services` 使用按插件隔离的能力。

| 目标 | 文档 |
| --- | --- |
| 从零编写 DLL | [C# 完整开发教程](csharp/DEVELOPMENT.md) |
| 查类型化服务/Polly | [宿主能力参考](csharp/README.md) |
| 阅读真实案例 | [ForwardAPI 源码](../src/Plugins.ForwardAPI/ForwardApiTerminal.cs) |
| 理解宿主运行 | [生命周期](HOST-LIFECYCLE.md) |
| 交给 AI 实现 | [AI 开发工作单](AI-DEVELOPMENT.md) |
| 查公共接口 | [宿主 Contracts 源码](https://github.com/NNNNolan/Router2API/tree/main/src/Router.Contracts) |

独立编译和插件单元测试使用 nuget.org 上的 `Router.Contracts` 2.0.0。事实来源是 `Router2API/src/Router.Contracts`；升级包版本后应回归，不能在插件或其单元测试中引入宿主内部实现来替代 API。首次还原见 [C# 教程](csharp/DEVELOPMENT.md)。

```powershell
dotnet build Plugins.slnx --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false
pwsh -File ./build.ps1
# 仅测试插件自身，无需检出宿主。
pwsh -File ./test.ps1
```

Linux Bash 对应运行 `dotnet build Plugins.slnx`，或直接执行 `bash ./build.sh`、`bash ./buildall.sh` 和 `bash ./test.sh`。

JS 类型、工具和完整教程位于独立的 `Rouer-Plugins-js/sdk/js`。原生 DLL 不受安全沙箱保护；只安装可信代码。完整构建/安装/测试说明见[根目录 README](../README.md)。
