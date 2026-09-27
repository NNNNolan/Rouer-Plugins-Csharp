# Rouer-Plugins-Csharp

Router2API 的 C# 提供方插件仓库，目标框架 .NET 10、公共契约 2.0。插件通过 `IPluginHost.Services` 使用账号、代理池、状态和任务能力，不包含宿主/数据库/前端管理系统的实现代码。

## 插件

| 安装目录 / PluginKey | 源码与说明 |
| --- | --- |
| `forwardapi` | [ForwardAPI](src/Plugins.ForwardAPI/README.md)：兼容站点账号、模型/端点允许表、原始协议转发 |

本仓库公开 **Plugins.ForwardAPI**，开发教程也以它为主例。使用 `Plugins.slnx` 编译，或通过 `build.ps1` 生成单插件发行包；`buildall.ps1` 会扫描本地所有插件项目并分别打包，包括存在于本机但被 Git 忽略的项目。

## 独立构建和打包

需要 .NET 10 SDK、PowerShell 7 或 Linux Bash。[NuGet.Config](NuGet.Config) 只配置公开的 nuget.org 包源；`Router.Contracts` 发布到 nuget.org 后，无需包源账号、PAT 或环境变量。详细步骤见 [C# 教程](sdk/csharp/DEVELOPMENT.md)。

```powershell
# 仅编译：不需要检出宿主仓库。
dotnet build Plugins.slnx --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false

# 生成 ForwardAPI 发行包，不复制到宿主、不执行任务。
pwsh -File ./build.ps1

# 扫描 src 下的 Plugins.*.csproj，分别打包所有插件。
pwsh -File ./buildall.ps1 -OutputDirectory ./artifacts/all-plugins
```

Linux Bash 对应命令：

```bash
dotnet build Plugins.slnx --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false
bash ./build.sh
bash ./buildall.sh --output-directory ./artifacts/all-plugins
```

输出：

```text
artifacts/plugins/
  forwardapi/Plugins.ForwardAPI.dll
```

每个目录还包括相应 `.deps.json` 和所需私有依赖。打包器排除 `Router.Contracts.*`，避免把 SDK 依赖描述当作第二个主 DLL。目标目录非空会拒绝打包；下一版使用 `pwsh -File ./build.ps1 -OutputDirectory ./artifacts/release-1.0.1`，不把新旧包混在一起。

项目填写 `.csproj` 的 `Description` 后，`build.ps1` 会在发行包内生成 `plugin.json`，手动复制到宿主时也能显示插件描述。旧包没有此文件时，宿主会尝试读取 DLL 的程序集描述或入口类型的 XML 文档摘要。

新增插件按 `src/Plugins.<名称>/Plugins.<名称>.csproj` 命名即可被 `buildall.ps1` 发现；发行目录名为 `<名称>` 的小写形式，并应与插件声明的 `PluginKey` 一致。脚本不依赖 `Plugins.slnx` 的项目清单。

## GitHub Release 发行索引

推送 `v1.2.3` 形式的 tag 后，GitHub Actions 会在干净检出中调用 `buildall.ps1`，为每个公开插件生成独立的 `<pluginKey>.zip`，并将 `release-index.json` 一同上传到该 tag 的 GitHub Release。每个插件项目的 `.csproj` 必须填写 `Description`；它会进入索引，并在 Release 正文中与插件版本、下载文件一同展示。正文末尾附仓库变更记录。ZIP 内保留 `<pluginKey>/` 顶层目录，解压到宿主 `plugins/` 后才是正确的安装结构。工作流只使用仓库自带的 `GITHUB_TOKEN`，需要允许 Actions 创建 Release 的 `contents: write` 权限。

Release 标题为 `C# 插件 <tag>`。如需写本次发布的专属说明，在打 tag 前提交 `release-notes/<tag>.md`，例如 `release-notes/v1.2.3.md`；工作流会把它放在插件介绍与自动生成的仓库变更之间。没有该文件也能正常发布。插件的长期描述仍在各自项目的 `Description` 中维护。

索引字段和宿主订阅建议见 `Router2API/sdk/PLUGIN-RELEASES.md`。`sha256` 校验 ZIP 下载，`contentSha256` 按包内文件内容判断该插件是否真的有更新。索引中的 C# `version` 读取入口 DLL 的程序集版本；Release 的 `tag` 是本仓库整批产物的发布标识，二者可以不同。新增公开插件时应同时确认其源文件确实被 Git 跟踪；本机被 Git 忽略的项目不会出现在 Actions 的干净检出中。

本地只生成待上传产物可运行 `pwsh -File ./buildall.ps1 -OutputDirectory ./artifacts/plugins`，随后运行 `pwsh -File ./package-release.ps1 -Tag v1.2.3`。后一个脚本要求输出目录为空，避免把前一次的资产混入新 Release。

复制**单个完整发行目录**到宿主运行目录的 `plugins` 后，再从管理页重载。不要直接复制混合的解决方案输出目录；不手工清理宿主正在使用的 `.staging`。

## Contracts 依赖

插件引用 nuget.org 上的 `Router.Contracts` 2.0.0；本仓库不再保存 Contracts 源码快照。GitHub Packages 上已有的同名包不会自动同步，需先完成 nuget.org 发布。

- 契约事实来源是 `Router2API/src/Router.Contracts`；插件升级包版本后需回归兼容测试。
- 运行时 DLL loader 始终使用宿主自己的 Contracts，不能用插件内自带版本替换宿主类型身份。
- 插件构建和单元测试均通过 NuGet 包获取契约，不引用或复制 `Router.Host`、`Router.Infrastructure` 等宿主实现。

## 测试

`tests/Router.Tests` 只测试插件自身行为，当前用例验证 ForwardAPI 每日签到任务的名称和 Cron 声明。宿主能力通过 `Router.Contracts` 接口 Mock，不依赖宿主源码、数据库、Redis、Node 或真实上游。

```powershell
pwsh -File ./test.ps1
# 或直接运行测试项目：
dotnet test ./tests/Router.Tests/Router.Tests.csproj --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false
```

Linux Bash 使用 `bash ./test.sh`。

这里的“仓库根目录”指 `Rouer-Plugins-Csharp`，不是包含三个仓库的工作区父目录；从父目录运行时使用 `pwsh -File ./Rouer-Plugins-Csharp/test.ps1`。
IDE 和命令行均可直接运行测试项目，无需配置宿主路径；只编译插件使用 `Plugins.slnx`。

宿主 DLL 加载、任务发现、通用执行器、Jint 和宿主 SSE/生命周期不属于本仓库测试范围，应在宿主仓库验证。插件单元测试通过不代表真实宿主集成或上游业务已验收。

## 教程

- [C# 完整开发流程](sdk/csharp/DEVELOPMENT.md)
- [类型化宿主能力与 Polly](sdk/csharp/README.md)
- [ForwardAPI 实际源码](src/Plugins.ForwardAPI/ForwardApiTerminal.cs)
- [宿主生命周期](sdk/HOST-LIFECYCLE.md)
- [AI 开发工作单](sdk/AI-DEVELOPMENT.md)

文档中跨仓库源码引用按 `仓库名/路径` 标识；独立构建和单元测试不需要宿主源码，实际安装联调才需要宿主。

## 公开与许可

不包含实际配置、账号、日志、数据库、User Secrets、编辑器文件或私有 Git 历史。不得强制提交 `.local-only`。没有替维护者选择新的主项目许可证，公开发布前请确认授权方式。
