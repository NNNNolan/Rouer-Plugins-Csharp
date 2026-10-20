# C# 开发教程：以 Plugins.ForwardAPI 为完整案例

本教程直接讲解本仓库发布的 **Plugins.ForwardAPI**，不以 Echo 或未发布提供方作为主例。目标框架 .NET 10、Contracts 2.0。构建和单元测试从 nuget.org 还原 `Router.Contracts` 2.0.0，不需要宿主源码。

入口：[SDK 总览](../README.md) · [能力参考](README.md) · [宿主生命周期](../HOST-LIFECYCLE.md) · [AI 工作单](../AI-DEVELOPMENT.md)。

## 1. 插件与宿主的职责

ForwardAPI 将 NewAPI、Sub2API 和自定义兼容站点统一为 `forwardapi/<模型 ID>`：

- 插件负责账号设置、模型/端点允许表、权重、上游协议、额度和签到。
- 宿主负责账号租约、代理能力、重试执行、认证、最终 HTTP 写出和卸载。
- 账号独立选择 `direct`、`pool` 或 `custom` 出口，覆盖模型转发、模型发现/刷新、额度、登录和签到；新旧账号默认直连。`upstreamApi` 默认 `original` 保持原协议，也可强制 Responses 或 Chat Completions 并转换请求/响应、工具和 SSE，保持下游协议。
- 管理页发现模型、保存账号、刷新候选/额度、手动签到。
- 每日 10:10 的 Cron 处理启用且开启自动签到的账号。

公开方案收录 ForwardAPI 和 ModelRotator，本教程以 ForwardAPI 为例。其他本地提供方保留在原 src 目录，由忽略的 `Plugins.All.slnx` 在本地完整编译；不进入公开方案、发行或 Git 提交。

## 2. 按调用链阅读源码

| 文件 | 重点方法 | 职责 |
| --- | --- | --- |
| [主终端](../../src/Plugins.ForwardAPI/ForwardApiTerminal.cs) | `Configure`、`GetModelsAsync`、`IsAccountEligible`、`RebuildAccountModelSnapshotAsync` | 元数据、策略、模型快照、生命周期 |
| [账号管理](../../src/Plugins.ForwardAPI/ForwardApiTerminal.Accounts.cs) | `SaveAccountAsync`、`DiscoverModelsAsync`、`RefreshModelsAsync`、`RefreshQuotaAsync` | 校验、持久化、模型发现、额度 |
| [请求与任务](../../src/Plugins.ForwardAPI/ForwardApiTerminal.Requests.cs) | `InvokeAsync`、`FailureDecision`、`ExecuteCheckInAsync` | 转发、错误动作、原始流、签到 |
| [请求转换](../../src/Plugins.ForwardAPI/ForwardApiTerminal.ProtocolRequest.cs) | 跨协议请求映射与校验 | 消息、工具、token 上限和结构化输出 |
| [响应转换](../../src/Plugins.ForwardAPI/ForwardApiTerminal.ProtocolResponse.cs) | 上游响应/SSE 解析 | 生成 canonical completion/chunk，交给宿主按下游协议写出 |
| [代理出口](../../src/Plugins.ForwardAPI/ForwardApiTerminal.Proxy.cs) | `TryApplyProxySettings`、`TryValidateProxySettings`、`CreateAccountClientAsync` | 代理字段合并/校验、管理请求客户端和指定代理客户端 |
| [内嵌页面](../../src/Plugins.ForwardAPI/ForwardApiTerminal.Page.cs) | `CreateMainPage` | 管理员 iframe 页面和请求桥 |
| [项目文件](../../src/Plugins.ForwardAPI/Plugins.ForwardAPI.csproj) | `PackageReference` | 只引用 Contracts 包 |

先沿一个请求读完：页面 `models/discover` → `FetchModelsAsync` → 保存允许模型 → `GetModelsAsync` → 宿主筛选 → `InvokeAsync` → 原协议 raw 响应或跨协议 canonical 响应 → 宿主写出/释放。跨协议测试既要断言插件构造的上游请求，也要在宿主测试中断言最终 JSON/SSE；只检查 `AdapterCompletion` / `StreamChunk` 无法验证公共协议的结束原因、截断状态及错误输出。

账号卡片通过 `POST accounts/enabled`（`SetAccountEnabledAsync`）独立启停。它仅修改设置开关和宿主状态，保留密钥、代理、模型及未知扩展字段，不重新发现模型；保存后重建快照并调用 `Models.Invalidate`。模型查询每次核对宿主账号状态，聚合启用且非 Disabled/Invalid 的账号模型，独有模型随禁用移除，共享模型保留；请求资格判断也优先拒绝当前 Disabled/Invalid 状态，防止旧快照绕过禁用。

## 3. 构建与依赖

仓库根目录的 [NuGet.Config](../../NuGet.Config) 只配置公开的 nuget.org 源。`Router.Contracts` 发布到 nuget.org 后，开发者无需 GitHub PAT、包源用户名、环境变量或 `.env`，PowerShell 7 中直接还原：

```powershell
dotnet restore ./Plugins.slnx
```

Linux Bash 同样运行 `dotnet restore ./Plugins.slnx`。若还原提示找不到 `Router.Contracts` 2.0.0，请先确认该版本已发布到 nuget.org 并完成索引；目前 GitHub Packages 上的同名包不会自动同步到 nuget.org。

### 在 VS Code 中拉取包

1. 安装 .NET 10 SDK 和 VS Code 的 **C# Dev Kit**，打开本仓库根目录 `Rouer-Plugins-Csharp`，不要只打开 `src/Plugins.ForwardAPI`。
2. 在 VS Code 集成终端运行 `dotnet restore ./Plugins.slnx`。无需设置任何包源凭据，也无需重启 VS Code 来传递环境变量。
3. 还原成功后，C# Dev Kit 的解决方案资源管理器会显示项目的 `Router.Contracts` 包引用；打开 C# 文件即可获得类型提示。若显示 NU1101，请确认包已发布到 nuget.org；若显示 NU1301，请检查网络连接和 nuget.org 可访问性。

从本仓库根目录继续构建，PowerShell 7：

```powershell
dotnet build Plugins.slnx --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false
pwsh -File ./build.ps1 -OutputDirectory ./artifacts/forwardapi-release
# 打包 src 中所有 Plugins.*.csproj 项目（每个插件独立目录）：
pwsh -File ./buildall.ps1 -OutputDirectory ./artifacts/all-plugins
```

Linux Bash 对应命令：

```bash
bash ./build.sh --output-directory ./artifacts/forwardapi-release
bash ./buildall.sh --output-directory ./artifacts/all-plugins
bash ./test.sh
```

单插件输出为 `artifacts/forwardapi-release/forwardapi`；批量输出在 `artifacts/all-plugins/<pluginKey>`。每个目录包括主 DLL、`.deps.json`、PDB/XML 和所需私有依赖。脚本排除 `Router.Contracts.*`，避免 loader 误判多个主程序集。目标目录非空时拒绝合并，下一次使用新发行目录。

插件和单元测试共用 `Router.Contracts` 包，均可独立运行，不引用宿主实现。测试中通过 Contracts 接口 Mock 所需能力，不要复制或引用 `Router.Infrastructure` 来解决缺失 API。

安装到宿主前，确认宿主已用 Contracts `2.0.0.0` 重新构建并部署；插件引用的包程序集版本为 `2.0.0.0`，旧宿主的 `1.0.0.0` 无法满足该程序集引用。

## 4. 终端如何注册

实际声明：

```csharp
[PipelinePlugin(PipelineStage.Terminal, 100, "ForwardApiTerminal")]
[PlatformAdapter("forwardapi", PluginKey = "forwardapi", DisplayName = "ForwardAPI")]
[ModelCache(Disabled = true)]
[CredentialSchema(CredentialKind.Custom)]
public sealed partial class ForwardApiTerminal(IPluginHost host)
    : IPlatformTerminal, IPluginModule, IPluginMainPageProvider,
      IPluginScheduledTaskProvider, IDisposable
```

构造时保存 `host.Services`。PluginKey 等于安装目录名，平台名是模型前缀。入口 DLL 由目录同名程序集或唯一 `.deps.json` 识别。

`Configure` 只声明规则，不联网。`StartAsync` 建立账号快照，但新版本尚未对外发布，不能递归刷新自己的模型目录。`StopAsync` 调用 Dispose 释放同步资源。

宿主对已进入 start 的 `IPluginModule` 只调用 Stop，不再补 Dispose；候选在 start 前失败时才走 IDisposable/IAsyncDisposable。清理必须支持部分初始化状态。

## 5. 账号结构和编辑流程

凭据为 `CustomCredential`，Fields 的值均为字符串或 null：

| 字段 | 内容 |
| --- | --- |
| `settings` | siteType、baseUrl、apiKey、username、password、weight、endpoints、enabled、autoCheckIn、extraParams、upstreamApi、proxyMode、proxyUrl、proxyUsername、proxyPassword 的 JSON |
| `models` | 管理员允许使用的模型数组 JSON |
| `modelsConfigured` | 字符串 `true`，表示明确配置过允许表 |
| `availableModels` | 最近发现的候选目录，不等于允许表 |
| `modelsUpdatedAt` | 目录更新时间 |
| `quotaSnapshot` / `quotaUpdatedAt` | 额度快照与更新时间 |

管理员操作：

1. 新建账号，填写名称、站点类型、Base URL 和 API Key。
2. 调用 `models/discover` 读取上游 `/v1/models`。
3. 选择允许模型、端点和权重，再调用 `accounts/save`。
4. 修改站点/API Key/额外参数后重新发现；编辑时密钥、密码留空保持原值。
5. `models/refresh` 只刷新候选，不自动扩大允许表。

`upstreamApi` 独立于端点允许表，合法值为 `original`（默认）、`responses`、`chat`。`accounts/save` 使用 `NormalizeUpstreamApi` 校验，未提交或为 `null` 时沿用已有值，非法值返回 400；账号卡片返回规范化后的值。页面“上游 API”选项只改变生成请求的上游协议，不改变下游入口，也不触发模型重新发现。`models/discover` 可接受此字段，但始终查询 `GET /v1/models`。

`proxyMode` 默认为 `direct`，另可选择宿主代理池 `pool` 或指定代理 `custom`。`proxyUrl` 为不含凭据的 `http://host:port`、`https://host:port` 或 `socks5://host:port`，认证通过 `proxyUsername` / `proxyPassword` 单独保存。保存和发现模型的四个代理输入字段为 `null` 或未提交时保留已有值；密码为空也保留，但切换出口或变更地址、用户名后会清除旧代理密码。仅修改代理设置不要求重新发现模型，也不清除候选目录和允许表。

`ToAccountCard` 不直接返回 API Key、站点密码或代理密码；代理密码仅返回 `hasProxyPassword` 状态。但额外参数可能包含操作者填写的敏感值，不能把管理响应公开或写入日志；JS 案例额外提供了秘密掩码回填。

当前 C# 保存主要使用 `Accounts.SaveAsync`。扩展并发刷新时优先使用 `PatchAsync` / `CompareExchangeCredentialAsync`，不要复制旧账号覆盖并发更新的账号状态，也不要宣称现有所有更新已经使用 CAS。

## 6. 模型、选号和缓存

主终端的真实策略：

```csharp
builder.AccountPolicy(policy => policy
    .SelectForRequest((account, request) => IsAccountEligible(account, request))
    .WeightBy((account, _) => GetAccountModelPolicy(account)?.Weight ?? int.MinValue));
```

资格同时检查 enabled、当前端点和模型允许表。权重只排序合格候选，不能绕过宿主的归属、停用、冷却和租约。

`RebuildAccountModelSnapshotAsync` 在锁内读取账号、构造不可变快照，再一次发布。Redis 是此插件可选的模型缓存，故障可回退已有账号快照；这不代表 SDK Shared 状态自动变成 Local。

宿主模型缓存被禁用，插件自行维护快照。保存/删除后重建并失效目录，内部模型 ID 不重复添加 `forwardapi/`。

## 7. 一次生成请求

```text
/v1 请求 → 宿主解析 platform/model → 选择账号
 → InvokeAsync → 校验允许表和 OriginalBody
 → 移除 endpoint/overrides/models，重写 model
 → 按 upstreamApi 保留原协议或转换到 Responses/Chat Completions
 → 按账号 proxyMode 选择出口：direct/pool 使用 attempt HttpClient，custom 使用指定代理客户端
 → 解释失败，原协议返回 raw；跨协议解析为 canonical completion/chunk
 → 宿主执行一次动作/决定下一次 attempt
 → writer 写出，最终释放
```

支持 Chat Completions、Completions、Responses、Messages 四种入口。`BuildUri` 保留部署子路径、消除重复 `/v1`，`/api/*` 从末尾 `/v1` 的父目录开始。

Messages 默认 x-api-key 和 anthropic-version，其他默认 Bearer；高级参数可配置认证头/前缀，不得覆盖危险连接头。模型获取/刷新（`GET /v1/models`）和转发都在发送前最后应用当前账号的 `ReplaceHeaders`，按头名不区分大小写替换或新增；示例和校验规则见[插件说明](../../src/Plugins.ForwardAPI/README.md#高级参数)。额度查询、登录和签到不使用这组覆盖项。

`original` 模式保留原始请求和响应字段；强制 `responses` / `chat` 时转换支持的消息、工具调用/结果、响应和 SSE，并向调用方保持原下游协议。不能把复杂请求简化为纯文本 messages，也不能静默丢弃无法表达的字段。所有协议专属扩展、原生文件 ID、Responses 状态续接（如 `previous_response_id`）等语义无法保证跨协议保留；遇到不支持的输入返回 400，需要完整原生语义时使用 `original`。raw 不承诺自动清洗上游错误正文中的秘密；日志必须另外脱敏。

## 8. 错误动作与 Polly

`InvokeAsync` 在 `InvokeCoreAsync` 外生成显式决策：

| 情况 | 行为 |
| --- | --- |
| 401 / 明确无效凭据的 403 | 停用当前账号，申请下一次 attempt |
| 429 / 5xx | 申请下一次 attempt，不冷却账号或代理 |
| 408 / 425 | 申请下一次 attempt，不冷却账号或代理 |
| 实际传输故障 | 申请下一次 attempt 和代理冷却，不冷却账号；宿主执行节点动作仍须真实代理证据 |
| 407 | 申请代理冷却，不冷却账号；节点动作由宿主核实代理证据 |
| 本地配置/允许表错误 | 失败，不解释为代理坏节点 |
| 成功或已返回流 | 不处罚，不拼接第二次生成请求 |

预算为最多 3 次、单次 setup 60 秒、总 setup 180 秒，宿主可以收紧。不得在模型 POST 外再套通用 HTTP retry。

模型请求的 `direct` / `pool` 分别向当前 attempt 客户端传 `useProxyPool:false` / `true`，代理池的直连回退沿用宿主全局设置。管理和任务请求通过 `CreateAccountClientAsync` 选择宿主直连客户端、`Services.Http.Pool` 或指定代理；管理代理池显式设置 `AllowDirectFallback = false`。指定代理不走系统代理，不回退到直连或代理池，也不增加自动重试。

## 9. raw/SSE 所有权

- `original` 非流式读取原始字节，usage 只供统计，正文不重新序列化；强制模式将上游响应转换为原下游协议。
- 原协议流式返回 `IsRawPassthrough` / `RawStream` 并保留 SSE 事件；跨协议流式返回 canonical `StreamChunk`，由宿主转换文本、工具及结束事件并保持下游协议。
- 原始迭代器在 finally 释放响应；宿主 scope/lifetime 负责取消和最终写出。
- 禁止返回惰性流前先 Dispose response。指定代理的 `ProxyResponseLifetime` 同时持有客户端和响应，通过 `AdapterResponse.Lifetime` 交接，覆盖未开始枚举的情况；流结束、取消或响应被释放时一并回收。
- 最后 marker 写失败不算成功，不能在 Invoke 返回时抢先统计完成。

深入阅读路径仍保留：`Router2API/src/Router.Infrastructure/Services/PluginResponseLifetime.cs`、`Router2API/tests/Router.Tests/StreamLifecycleTests.cs`。这只是源码阅读，不是插件引用宿主实现。

## 10. 额度和签到

### 额度

NewAPI `/api/usage/token`，Sub2API `/v1/usage`，404/405 可尝试尾斜杠；解析 quota、subscription 周期、rate_limits、usage.total.cost。NewAPI `/api/status` 决定 quota_per_unit 与货币单位。

金额用 decimal；失败不是余额 0，失败时间不能作为成功更新时间。新增站点先写真实字段、单位、无限额和认证规则的测试。Custom 没有通用额度接口，明确返回不支持。

### 签到

`ExecuteCheckInAsync`：配置校验 → 可选登录 → token/Cookie/UID → 模板替换 → 签到 → Success/Already/Skipped/Failed → 逐账号日志。

NewAPI 默认 `/api/user/checkin`、`/api/user/sign_in`，需用户名/密码；其他站点显式配置路径。仅路径明确不可用才换备用地址，普通错误/超时不重放 POST。任意 HTTP 200 不能算成功。

Cron 名称 `forwardapi-daily-checkin`，表达式 `0 10 10 * * *`，中国标准时间。原生锁 TTL 30 分钟且不续租，不是无限时长跨实例恰好一次保证。

## 11. 页面与安装调试

页面使用 `window.Router2API.request(method, relativeRoute, body)`，没有后端 ctx，不直接读取管理员 Cookie。实际页面具备账号编辑、删除确认、模型选择、额度展示；外部数据先转义。

管理接口：GET `accounts`；POST `accounts/save`、`accounts/enabled`、`accounts/delete`、`models/discover`、`models/refresh`、`quota/refresh`、`checkin/run`。均需宿主管理员会话/CSRF。

安装到 `AppContext.BaseDirectory/plugins/forwardapi`，不是随意工作目录：

```powershell
$package = Resolve-Path ./artifacts/forwardapi-release/forwardapi
$plugins = Read-Host '输入隔离测试宿主 plugins 目录的绝对路径'
if (Test-Path -LiteralPath (Join-Path $plugins 'forwardapi')) { throw '已有版本：先备份并安排替换窗口，不直接合并' }
Copy-Item -LiteralPath $package.Path -Destination $plugins -Recurse
```

管理页重载，确认 dotnet/Active/版本和模型，再调用 `forwardapi/<允许模型>`，测试 stream 两种模式及四种入口。调试附加到宿主进程，保留匹配 PDB，不运行类库自身。

## 12. 测试和交付

```powershell
pwsh -File ./test.ps1
```

测试只验证插件自身，不包含宿主 DLL 加载器、任务发现或通用运行时测试，也不需要宿主源码。当前用例检查每日签到任务声明，以及 `ReplaceHeaders` 在四种端点/流式模式中的覆盖、新增、账号隔离和非法配置拒绝；后续按插件改动补允许表、凭证保持、模型失败、原始字段、错误动作、插件流取消、未知签到响应、额度单位和并发更新用例，外部能力通过 Contracts 接口 Mock。

`git status` 只应包含 ForwardAPI、包引用、NuGet.Config、文档和对应测试。不要用 `git add -f` 带回 `.local-only` 的其他提供方。宿主仓库使用 NuGet Trusted Publishing 获取临时发布凭据；本教程不自动发布。
