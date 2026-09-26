# C# 插件宿主边界

首次开发请先读 [ForwardAPI 开发全过程](DEVELOPMENT.md)，对照 [实际终端源码](../../src/Plugins.ForwardAPI/ForwardApiTerminal.cs)。本文是能力参考；运行顺序见 [宿主运转流程](../HOST-LIFECYCLE.md)，AI 协作见 [开发工作单](../AI-DEVELOPMENT.md)。

原生插件仍实现 `IPlatformTerminal`，构造参数仍为 `IPluginHost`。本轮对契约 2.0 是增量扩展：旧 DLL 的 `GetServiceAsync<T>`、旧 `PluginAttemptResult` 和策略 predicate 保留兼容；新代码使用 `host.Services`。这不是原生 DLL 的安全沙箱。

## 类型化能力

| 入口 | 所有权和范围 |
| --- | --- |
| `Services.Accounts` | 只读写本插件账号，无 `GetAnyAsync` 或任意 `PluginKey` 参数；刷新不得改变标识/平台 |
| `Services.Http` | 显式直连和独立 `Pool`；客户端/响应由调用方释放 |
| `Services.State.Local` | 本版本内存状态，128 字符 key、64 KiB/value、256 项、TTL 最多一天；与 JS `ctx.state` 相同 |
| `Services.State.Shared` | 可选 Redis、插件命名空间，128 字符 key、4 MiB/value、TTL 最多 30 天，无静默内存回退 |
| `Services.Models` | 本插件声明的平台目录/失效，及公共只读模型元数据 |
| `Services.Tasks` | 本插件已注册任务及账号任务明细日志，任务锁/调用取消由宿主执行 |
| `Services.Jobs` | 本版本命名后台任务的启动/去重/状态/进度/取消，卸载时取消并等待 |
| `Services.Log` / `Services.LogAsync` | 绑定本插件的日志，不允许伪造其他插件归属 |

保存已解除冷却的账号时，宿主负责同步清理短期冷却标记。插件不再依赖 `account:cooldown:*` 等宿主 Redis 键。共享模型缓存会因命名空间收拢而重新预热；持久化账号不迁移。

### 定时任务使用代理池

```csharp
var services = host.Services; // 保存到插件实例字段
using var response = await services.Http.Pool.SendAsync(
    () => new HttpRequestMessage(HttpMethod.Get, "https://example.com/status"),
    retry: new ProxyPoolRetryOptions { MaxRetries = 2 },
    cancellationToken: context.CancellationToken);
var body = await response.Content.ReadAsStringAsync(context.CancellationToken);
```

默认无重试、无自动重定向、无共享 Cookie、无系统代理回退。无可用代理会失败；直连回退必须显式配置。
HTTP 401/407/429/5xx 原样返回，不换账号、不惩罚节点。POST 等非安全方法只有明确 `AllowUnsafeMethods = true` 才能重试，且每次由 factory 创建新请求、重新选择代理。

**模型调用使用 `PluginAttemptContext.HttpClient`，不要再套通用 HTTP retry。** 模型 POST 的业务恢复由插件识别并返回明确决策；不要把账号重试和传输重试相乘。SSE body 失败不自动重新发送生成请求。
该 attempt 客户端现在统一禁止自动重定向，避免绕过 JS origin 权限或隐式改变生成请求的方法。管理/模型发现直连如需要跳转，可显式使用 `CreateDirectClient(new() { AllowAutoRedirect = true })`；内置 C# 插件在这些非生成路径保留了此选项。

## 一次性业务决策

```csharp
var attempt = new PluginAttemptDecision
{
    FailureKind = PluginFailureKind.Upstream,
    Retry = PluginRetryAction.NextAttempt,
    AccountAction = PluginAccountAction.Cooldown,
    AccountCooldownUntil = DateTimeOffset.UtcNow.AddMinutes(5),
    AccountReason = "quota exhausted",
    ReasonCode = "quota.exhausted"
}.ToResult(statusCode: 429, reason: "upstream error");

return new PluginInvocationResult(
    new AdapterResponse { StatusCode = 429, Error = "quota exhausted" },
    attempt);
```

有显式决策时不再执行任何旧 predicate。宿主校验枚举、最长 30 天账号冷却、当前资源归属和预算；`Reason`/`ReasonCode` 不参与宿主业务判断。
节点冷却还需要当前原生尝试确实出现 HTTP 传输异常或代理 407，插件声明的 bool/状态码不能替代证据。成功响应不惩罚资源，返回流后不重试。

`Configure` 现在主要声明账号筛选、权重和预算。可用 `OnTransportFailure(() => decision)` 为宿主实际 HTTP 异常提供决策；其中时间应在委托调用时计算，不要把冷却截止时间固定在启动时。
ForwardAPI 在 `FailureDecision` 中解释 401/403/429/5xx，返回动作，不在同一模型错误分支先写一次冷却再让宿主重复执行。

字段级更新使用 `Accounts.PatchAsync(account, fields)`，只更新列出的 JSON 字段路径；`CompareExchangeCredentialAsync` 使用 `CredentialVersion` 防止旧凭据覆盖，并且不覆盖冷却状态。`RefreshCredentialAsync` 在本宿主账号刷新锁下执行回调，最后 CAS 提交。

后台任务通过 `builder.Job(new PluginJobRegistration(...))` 注册，使用 `Services.Jobs.StartAsync/Get/List/WaitAsync/CancelAsync`。输入/结果为 JSON，任务参数中的 `ReportProgress` 提供进度；作用域属于包版本，必须在卸载前取消并排空。模型当前请求令牌只取消入队或等待，不隐式取消已创建的后台任务。

### ForwardAPI 的允许表和权重

```csharp
builder.AccountPolicy(policy => policy
    .SelectForRequest((account, request) => IsAccountEligible(account, request))
    .WeightBy((account, _) => GetAccountModelPolicy(account)?.Weight ?? int.MinValue));
```

回调是 ForwardAPI 实际的方法，检查 enabled、当前端点和模型允许表，再按账号权重排序；读取本地快照，不同步等待 HTTP。SDK 另外支持 `PreferEarlier`，但当前 ForwardAPI 未使用它，不能把可用能力写成当前案例已经实现的规则。

## 加载与资源所有权

```text
DotNetPackageLoader / JintPackageLoader
  → LoadedPlugin（平台、端点 delegate、任务、页面、生命周期）
  → PluginCatalog（验证归属、启动、版本切换、drain、失败保留旧版本）
```

DLL 反射只在 loader 发生，端点/任务预绑定 delegate；端点认证仍由宿主统一处理。一个包的 `PluginKey` 必须与安装目录一致，不能抢占其他插件的平台。
模型缓存遵守 `[ModelCache]` / JS TTL；失效或更换版本后，旧的异步模型查询不能覆盖新缓存。

`AdapterResponse.Lifetime` 持有客户端、响应、租约和 JS Engine。调用方必须完整消费流/Dispose 枚举器，或者显式 `await response.Lifetime.DisposeAsync()`；HTTP 写出器自动处理。业务统计等到完整写出后完成。

## Polly 与执行配置

`PluginResiliencePipelines` 使用现有 Polly v8 registry 集中注册 Lambda：

- `plugin-attempt`：仅处理显式的业务 `NextAttempt`。
- `task-safe-read`：仅对安全方法的传输异常重试。
- `explicit-replay`：明确授权的非安全方法重放。
- 未启用重试直接执行，不为每次请求构建 pipeline，不按任意调用参数生成缓存 key。

调用状态和预算放在 `ResilienceContext`，不捕获到共享策略闭包。`ProxyTransportFactory` 共用实际连接配置的缓存；重定向选项单独分区，旧配置只在租约归还后淘汰，所有路径禁用共享 Cookie。
这里没有使用 `AddStandardResilienceHandler`：其默认状态码重试/POST 重放/超时不符合模型场景，也不管理 `ResponseHeadersRead` 后的 SSE 生命周期。

可以在 `Config/Config.json` / 环境变量配置以下快照预算（修改后重启）：

```json
{
  "Plugins": {
    "Execution": {
      "QueueTimeout": "00:00:02",
      "SetupTimeout": "00:01:00",
      "TotalSetupTimeout": "00:03:00",
      "ResponseTimeout": "00:10:00",
      "ReadIdleTimeout": "00:01:00",
      "MapperBudget": "00:00:00.100",
      "CompletionMapperBudget": "00:00:00.250",
      "EndpointTimeout": "00:00:30",
      "DrainTimeout": "00:00:10"
    }
  }
}
```

原生 `PluginInvocationScope` 统一建立响应前/响应体阶段的截止时间和所有权转移；JS 使用同一份预算。插件的单次/总超时只能进一步缩短。读取空闲超时不会自动重放流。
JS 账号 CAS、共享状态、后台 job 和原始协议能力使用相同宿主实现。JS 案例为 `Rouer-Plugins-js/plugins/js-forwardapi`，业务回归只使用 Node 模拟；真实上游仍需独立验收。C# 项目引用 `Router.Contracts` 2.0.0，首次还原步骤见[开发教程](DEVELOPMENT.md)。
