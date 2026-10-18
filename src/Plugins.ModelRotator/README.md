# ModelRotator

将一个虚拟模型映射到有序实际模型目标；实际模型调用失败时由插件内部有限轮转，成功后保持 sticky 目标或按配置优先级选择。手动激活只更新后续请求使用的 pin，不会关闭、重置或重放已经开始的连接。

## 功能

- 兼容 JS 版插件文档 v3：虚拟模型路由、故障/超时计数、健康状态、active pin、31 天 Token 用量（保留旧版紧凑数组行）。配置保存到插件 Custom Credential；仅读取到缺失配置时建立默认账户，读错误/损坏 JSON 不会覆盖现有配置。
- 支持 `/v1/chat/completions`、`/v1/completions`、`/v1/embeddings`、`/v1/responses`、`/v1/messages`。保留原始请求字段，仅替换实际模型并移除宿主内部路由字段。
- 最多尝试 5 个实际模型；客户端输入错误不增加故障计数，平台共享认证错误不会继续轮转。流式响应采用原始流透传，连接一旦开始由宿主生命周期负责，不做中途重试。
- 快速轮转：失败/超时计数还没清零的目标会排到候选队尾，不再按固定顺序每次第一个撞；对已经等满超时的目标，下一次只给 20 秒复检窗口（配置更短时按配置）；超时也不再上调首字节观测值，避免“越超时等越久”的正反馈。
- 定时探测已禁用的实际模型，可手动探测；支持按日查看 Token 用量。
- 探测的单目标超时可在设置里配置（5–180 秒，默认 45 秒），超时与“仅返回空内容”分别归类，不再用固定的 12 秒把慢目标误判为异常。
- 内嵌管理页面支持新增/编辑/排序虚拟模型目标、启停路由、手动切换当前 pin、平台回环/API Key 与等待预算设置。

## 手动切换语义

管理页面选择“当前活跃目标”后，插件原子更新配置中的 active pin。已进入宿主并开始向上游请求的连接继续由原有请求使用它已经选中的目标；不会断连、取消或切换该连接。pin 从后续新进入的请求开始生效。恢复自动选择会清除 pin，按健康状态和目标顺序重新选路。

## 探测语义

探测对每个目标发一条极小请求（chat 类 `max_tokens` 1024，其余 16），用于判断目标当前是否可用。

- **单目标超时**：设置项“单目标探测超时”，默认 45 秒，范围 5–180 秒。免费上游首字节常在 40–60 秒，12 秒级的固定超时会把慢但可用的目标误判为异常，所以这里按可配置值等待。
- **并发**：手动探测并发 4，避免一次性打爆同一批免费上游（并发越高，越容易互相拖慢并集体超时）；定时探测只针对已禁用目标，并发 8。
- **总预算**：手动探测 = 批次数 × 单目标超时 + 15 秒（20–300 秒封顶）；定时任务 = 240 秒。预算用尽时返回已完成的结果并标记 `remaining`，可再点一次继续。
- **结果分类**：`正常`（2xx 且有内容）、`结果不确定（空响应）`（2xx 但内容为空或仅 `finish_reason=length`，不会解除禁用）、`超时`（单目标超时，计入超时桶而非故障桶）、`异常`（非 2xx 或其他错误）。

## 管理与安全

管理端点通过宿主默认 AdminSession 认证，页面经 `window.Router2API.request` 调用相对路由。平台 Base URL 仅允许本机回环地址，避免把此代理能力变成任意 URL 请求；平台 API Key 只写入配置，不回显原文。

插件 Key / 平台名为 `modelrotator`。来自 JS 版的 `js-model-rotator` 配置记录只在宿主按当前插件 Key 提供旧账户时自动读取；如果宿主对账号按 PluginKey 隔离，须在宿主迁移数据时复制旧 Credential 到新插件账户空间，不能由公开插件绕过宿主账号边界。

## 版本号

版本号只有一个来源：`Plugins.ModelRotator.csproj` 的 `<Version>`。宿主管理页显示的是入口 DLL 的程序集版本（`assembly.GetName().Version`），`build.ps1` 也把同一个程序集版本写进发行包 `plugin.json`，所以两者天然一致。手工打包时不要另写版本号，也不要只改 `plugin.json`——只改清单会让页面继续显示旧版本，看起来像升级没生效。

## 构建与测试

从仓库根目录运行：

```powershell
dotnet build ./src/Plugins.ModelRotator/Plugins.ModelRotator.csproj --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false
dotnet test ./tests/Router.Tests/Router.Tests.csproj --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false
```
