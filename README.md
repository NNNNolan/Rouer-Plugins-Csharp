# Rouer-Plugins-Csharp

Router2API 的 C# 提供方插件仓库，目标框架 .NET 10、公共契约 2.0。插件通过 `IPluginHost.Services` 使用账号、代理池、状态和任务能力，不包含宿主/数据库/前端管理系统的实现代码。

## 插件

| 安装目录 / PluginKey | 源码与说明 |
| --- | --- |
| `forwardapi` | [ForwardAPI](src/Plugins.ForwardAPI/README.md)：兼容站点账号、模型/端点允许表、原始协议转发 |

本仓库提供 **Plugins.ForwardAPI**，开发教程也以它为主例。使用 `Plugins.slnx` 编译，或通过 `build.ps1` 生成发行包。

## 独立构建和打包

需要 .NET 10 SDK 和 PowerShell 7：

```powershell
# 仅编译：不需要检出宿主仓库。
dotnet build Plugins.slnx --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false

# 生成 ForwardAPI 发行包，不复制到宿主、不执行任务。
pwsh -File ./build.ps1
```

输出：

```text
artifacts/plugins/
  forwardapi/Plugins.ForwardAPI.dll
```

每个目录还包括相应 `.deps.json` 和所需私有依赖。打包器排除 `Router.Contracts.*`，避免把 SDK 依赖描述当作第二个主 DLL。目标目录非空会拒绝打包；下一版使用 `pwsh -File ./build.ps1 -OutputDirectory ./artifacts/release-1.0.1`，不把新旧包混在一起。

复制**单个完整发行目录**到宿主运行目录的 `plugins` 后，再从管理页重载。不要直接复制混合的解决方案输出目录；不手工清理宿主正在使用的 `.staging`。

## Contracts 为什么也在这里

`src/Router.Contracts` 是公共 SDK **源码快照**，不是宿主实现，保证单独 clone 本仓库就能编译，不依赖一个尚未发布的 NuGet 包。

- 契约事实来源是 `Router2API/src/Router.Contracts`，修改接口应先在宿主完成，再同步快照和兼容测试。
- 运行时 DLL loader 始终使用宿主自己的 Contracts，不能用插件内自带版本替换宿主类型身份。
- 默认构建使用本仓库快照；显式设置 `RouterHostRoot` 时使用该宿主检出的 Contracts，便于跨仓库测试，避免两个同名 SDK 项目冲突。
- 不通过复制 `Router.Host` 或 `Router.Infrastructure` 源码解决测试依赖。
- 后续计划由宿主 tag 自动发布 `Router.Contracts` 到 NuGet，再迁移到包引用；本轮保留现状，不创建发布工作流或引用不存在的包。

## 测试

部分测试需要真实宿主服务/加载器，因此测试时另行检出 `Router2API`，这一引用方式暂时不改。插件本身编译不需要宿主源码或 Node。

默认采用同级目录：

```text
workspace/
  Router2API/
  Rouer-Plugins-Csharp/
```

```powershell
pwsh -File ./test.ps1
# 或使用任意检出位置：
pwsh -File ./test.ps1 -RouterHostRoot /path/to/Router2API

```

集成测试包含 ForwardAPI 注册、任务发现和 DLL 加载，不用真实账号签到。宿主通用执行器、Jint、SSE/生命周期测试在宿主仓库运行。

## 教程

- [C# 完整开发流程](sdk/csharp/DEVELOPMENT.md)
- [类型化宿主能力与 Polly](sdk/csharp/README.md)
- [ForwardAPI 实际源码](src/Plugins.ForwardAPI/ForwardApiTerminal.cs)
- [宿主生命周期](sdk/HOST-LIFECYCLE.md)
- [AI 开发工作单](sdk/AI-DEVELOPMENT.md)

文档中跨仓库引用按 `仓库名/路径` 标识。远程地址尚未指定，不虚构仓库 URL；独立构建不需要这些外部文件，只有联调/集成测试需要宿主检出。

## 公开与许可

不包含实际配置、账号、日志、数据库、User Secrets、编辑器文件或私有 Git 历史。不得强制提交 `.local-only`。没有替维护者选择新的主项目许可证，公开发布前请确认授权方式。
