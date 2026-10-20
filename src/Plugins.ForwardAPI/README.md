# Plugins.ForwardAPI

`Plugins.ForwardAPI` 把兼容 OpenAI API 的 NewAPI、Sub2API 或自定义站点作为一个平台接入 Router2API。一个插件实例可以保存多个上游账号，并依据账号启用状态、允许的端点、模型列表和权重选择可用账号。

## 能力范围

- 转发 `/v1/chat/completions`、`/v1/completions`、`/v1/responses` 和 `/v1/messages`。
- 按账号配置的模型目录限制可路由模型；权重较高的合格账号优先。
- 默认保留原协议和上游响应内容。可按账号强制使用 Responses 或 Chat Completions，上游请求、响应、工具调用及 SSE 自动转换，保持下游调用方的协议。
- 提供账号管理页面，以及模型发现、模型刷新、NewAPI/Sub2API 额度刷新和手动签到接口。
- 支持 NewAPI 每日自动签到；Sub2API 和 Custom 站点需要显式配置签到路径。

每个账号可以选择“代理池”“指定代理”或“直连”，该出口设置覆盖账号的所有上游请求，包括四种模型转发（含流式）、模型发现/刷新、额度与站点状态查询、登录及手动/自动签到。新账号和未配置代理字段的旧账号均默认直连。认证、账号选择、重试结果、账号状态和代理冷却仍由 Router2API 宿主管理。无效凭据会停用账号；传输故障和 407 可申请代理冷却，ForwardAPI 不因这些错误或 429/5xx 冷却账号。

## 添加账号

在 ForwardAPI 管理页面添加账号并填写：

| 字段 | 说明 |
|---|---|
| 账号名称 | 管理页面中显示的名称。 |
| 站点类型 | `NewAPI`、`Sub2API` 或 `Custom`。额度查询和默认签到路径只对已支持的站点类型启用。 |
| Base URL | 绝对 `http` 或 `https` 地址，可填写站点根地址或 `/v1` 地址；不能包含用户信息、查询串或片段。 |
| API Key | 站点 API 密钥。编辑账号时留空表示沿用已保存密钥。 |
| 上游 API | `original`（原协议透传，默认）、`responses`（Responses）或 `chat`（Chat Completions）。独立于端点允许表，修改后无需重新发现模型。 |
| 请求出口 | `direct`（直连，默认）、`pool`（宿主代理池）或 `custom`（指定代理）。修改出口设置不要求重新发现模型，也不清除已保存模型目录或允许表。 |
| 代理地址、用户名、密码 | 仅指定代理时使用。地址格式为不带凭据的 `http://host:port`、`https://host:port` 或 `socks5://host:port`；认证信息单独填写。代理密码留空时，只有出口、地址和用户名均未变更才保留旧值。 |
| 权重 | `0` 到 `1000`；同一模型和端点有多个可用账号时，较高权重优先。 |
| 用户名、密码 | 用于站点网页登录签到；不是 API Key 的替代品。编辑时密码留空表示沿用原值。 |
| 允许转发的端点 | 至少选择一个支持的端点。账号只会参与所选端点的请求路由。 |
| 可用模型和允许模型 | 先从当前 API Key 拉取模型目录，再选择允许转发的模型。修改 Base URL、站点类型、API Key 或高级参数后需重新发现模型。 |
| 启用账号 | 可在账号卡片直接启用/禁用。禁用后立即退出模型池和请求路由，不执行自动签到；保留模型及连接配置，重新启用后恢复注册。 |
| 每日自动签到 | 仅在宿主定时任务运行时，对已启用且勾选此项的账号尝试签到。 |

模型目录从上游 `GET /v1/models` 获取。新账号保存前必须成功发现至少一个模型，并至少选择一个允许模型。模型发现和保存均使用表单中当前选择的代理设置。

禁用会移除该账号提供的模型注册；同名模型如仍有其他启用账号则继续保留，所有账号均禁用时该模型不再公布。模型目录查询同时核对宿主的 Disabled/Invalid 状态，避免无效凭据被宿主停用后仍显示陈旧模型。禁用不强制终止已在执行的请求。

站点类型不会自动改写生成请求协议，账号端点复选框只控制允许哪些下游入口。“上游 API”独立选择实际发送到上游的接口：`original` 保持原路径和协议；`responses` 使用 `/v1/responses`；`chat` 使用 `/v1/chat/completions`。例如下游调用 Chat Completions、账号选择 Responses 时，插件向上游发送 Responses 请求，再将响应或 SSE 转换回 Chat Completions，下游仍使用原来的协议。新账号与未配置此字段的旧账号均默认 `original`。

强制模式会转换支持的消息、工具调用/结果、响应和 SSE，但不能完整保留所有协议专属扩展、原生文件 ID 和 Responses 状态续接（如 `previous_response_id`）等语义。遇到无法表达或不支持的输入时返回 400，避免静默丢弃字段或改变其含义；需要完整原生语义时选择“原协议透传”。模型发现仍固定使用 `GET /v1/models`，不受上游 API 选择影响。

指定代理连接失败时直接返回错误，不回退到直连或代理池，也不使用系统代理。切换出口模式或修改代理地址、用户名时会清除旧代理密码；如仍需认证，请同时重新填写密码。账号列表只返回 `hasProxyPassword` 表示是否已配置代理密码，不回传密码内容。代理池使用宿主的可用节点；管理请求的代理池禁止直连回退，无可用代理时返回错误。模型请求的代理池沿用宿主全局直连回退设置。

## 高级参数

“额外参数”必须是 JSON 对象。常用字段如下，未配置的字段使用插件默认值：

| 字段 | 作用 |
|---|---|
| `apiKeyHeader`、`apiKeyPrefix` | 指定模型发现和请求转发使用的 API Key 请求头及前缀。默认头为 `Authorization`，默认前缀为 `Bearer `。Anthropic Messages 默认使用 `x-api-key`，除非显式覆盖。 |
| `ReplaceHeaders` | 当前上游账号的请求头覆盖对象。头名不区分大小写，已有头替换、缺失头新增；在账号认证头和协议默认头之后应用。用于模型获取/刷新（`GET /v1/models`）和四种模型转发端点（含流式），不影响额度、登录或签到。获取模型时使用本次表单参数（未提供的字段沿用账号配置），刷新时使用账号已保存参数。 |
| `quotaApiKeyHeader`、`quotaApiKeyPrefix` | 覆盖额度查询请求使用的认证头。未指定时额度查询默认使用 `Authorization: Bearer ...`。 |
| `checkInPath` | 指定签到相对路径。必须是站点内路径，不能是绝对 URL、协议相对 URL 或带 `..` 的路径。 |
| `checkInMethod`、`checkInBody`、`checkInHeaders` | 配置签到 HTTP 方法、JSON 请求体和请求头；方法默认 `POST`。请求体字符串可使用 `{{access_token}}`、`{{token}}`、`{{user_id}}` 和 `{{username}}` 占位符。 |
| `loginPath`、`loginMethod`、`loginBody`、`loginHeaders` | 配置签到前的网页登录。默认方法为 `POST`；默认请求体包含用户名和密码。登录请求体字符串可使用 `{{username}}`、`{{password}}`。登录返回的 Token 或 Cookie 会用于签到请求。 |

例如，保留已有的其他额外参数字段，并加入下面的配置替换 User-Agent：

```json
{
  "ReplaceHeaders": {
    "User-Agent": "claude-cli/2.1.161 (external, cli)"
  }
}
```

`ReplaceHeaders` 未配置或为空对象时保持原行为；配置值必须是字符串，显式配置的认证头也会覆盖默认值。拒绝控制字符及 Host、Connection、Content-Length、Cookie 等危险请求头。修改后按页面要求重新获取模型并保存账号。

NewAPI 未指定 `checkInPath` 时会按顺序尝试 `/api/user/checkin` 和 `/api/user/sign_in`，并且需要用户名和密码登录。Sub2API 或 Custom 未指定签到路径时会跳过签到，不猜测站点接口。

额度查询只支持 NewAPI 和 Sub2API。Custom 站点没有通用额度接口，因此额度刷新会返回不支持错误。

## 管理端点

以下路径是插件端点的相对路径，完整管理路由前缀由宿主注册配置决定。请求体均为 JSON。

| 方法 | 路径 | 用途 |
|---|---|---|
| `GET` | `accounts` | 列出已保存账号及模型、额度和签到配置。 |
| `POST` | `accounts/save` | 新增或更新账号。编辑时 `id` 指定现有账号；密钥和密码可留空以保留旧值。 |
| `POST` | `models/discover` | 用当前站点地址、API Key 和代理设置拉取可用模型目录；可传 `id` 读取现有账号参数。 |
| `POST` | `accounts/enabled` | 按 `{ "id": "...", "enabled": false }` 禁用账号并立即刷新模型池；设为 `true` 恢复。无需重新获取模型，不发送上游请求。 |
| `POST` | `accounts/delete` | 按 `{ "id": "..." }` 删除账号。 |
| `POST` | `models/refresh` | 按 `{ "id": "..." }` 刷新账号可用模型目录。 |
| `POST` | `quota/refresh` | 按 `{ "id": "..." }` 查询并保存 NewAPI/Sub2API 额度快照。 |
| `POST` | `checkin/run` | 按 `{ "id": "..." }` 手动执行单个账号签到。 |

`accounts/save` 接受 `upstreamApi`，合法值为 `original`、`responses`、`chat`；未提交或为 `null` 时沿用现有值，新账号默认为 `original`，非法值返回 400。账号卡片返回 `upstreamApi`。`models/discover` 可随表单携带该字段，但模型目录查询不依赖它。

`accounts/save` 与 `models/discover` 均接受 `proxyMode`、`proxyUrl`、`proxyUsername`、`proxyPassword` 四个字段。字段为 `null` 或未提交时沿用现有值；代理密码为空字符串也按上述保留/清除规则处理。新账号不提交这些字段时采用 `direct`。`GET accounts` 和保存返回的账号卡片包含 `proxyMode`、`proxyUrl`、`proxyUsername` 与 `hasProxyPassword`。

## 定时任务

插件注册任务 `forwardapi-daily-checkin`，Cron 为 `0 10 10 * * *`，即宿主任务时区每天 10:10 执行。任务仅处理启用且打开“每日自动签到”的账号，执行结果写入宿主任务日志。

## 实现位置

- `ForwardApiTerminal.cs`：平台、账号策略、模型快照、生命周期和定时任务注册。
- `ForwardApiTerminal.Requests.cs`：上游请求、原协议透传或转换后的响应/SSE 输出和签到执行。
- `ForwardApiTerminal.Accounts.cs`：账号管理、模型发现、额度查询和相关数据解析。
- `ForwardApiTerminal.Page.cs`：内嵌管理页面。

插件响应契约为 2.0。`original` 模式沿用原协议转发，通过 `RawContent` / `RawStream` 返回上游响应；转发前仍会应用模型映射和宿主字段过滤。跨协议转换使用共享的 `AdapterCompletion` / `StreamChunk`，由宿主按下游协议生成最终 JSON / SSE 并管理响应生命周期。

2.0.7 修复并行工具调用的消息分组，转换到 Chat 时使用 `max_completion_tokens`，并为流式请求启用 `stream_options.include_usage`。JSON Schema 格式省略 `strict` 时按非严格模式转换。完整的工具结束原因、截断/过滤状态和旧版 Completions 流错误输出还需要配套安装宿主协议输出修复；只更新插件无法修复宿主序列化行为。
