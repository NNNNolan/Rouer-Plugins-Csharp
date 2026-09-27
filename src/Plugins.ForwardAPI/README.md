# Plugins.ForwardAPI

`Plugins.ForwardAPI` 把兼容 OpenAI API 的 NewAPI、Sub2API 或自定义站点作为一个平台接入 Router2API。一个插件实例可以保存多个上游账号，并依据账号启用状态、允许的端点、模型列表和权重选择可用账号。

## 能力范围

- 转发 `/v1/chat/completions`、`/v1/completions`、`/v1/responses` 和 `/v1/messages`。
- 按账号配置的模型目录限制可路由模型；权重较高的合格账号优先。
- 保留上游响应内容。非流式响应和 SSE 流由插件透传，不在此插件中重写协议事件。
- 提供账号管理页面，以及模型发现、模型刷新、NewAPI/Sub2API 额度刷新和手动签到接口。
- 支持 NewAPI 每日自动签到；Sub2API 和 Custom 站点需要显式配置签到路径。

上游请求通过插件宿主提供的 HTTP 客户端发送。当前实现选择直连上游，不使用代理池。认证、账号选择、重试结果和账号冷却仍由 Router2API 宿主管理。

## 添加账号

在 ForwardAPI 管理页面添加账号并填写：

| 字段 | 说明 |
|---|---|
| 账号名称 | 管理页面中显示的名称。 |
| 站点类型 | `NewAPI`、`Sub2API` 或 `Custom`。额度查询和默认签到路径只对已支持的站点类型启用。 |
| Base URL | 绝对 `http` 或 `https` 地址，可填写站点根地址或 `/v1` 地址；不能包含用户信息、查询串或片段。 |
| API Key | 站点 API 密钥。编辑账号时留空表示沿用已保存密钥。 |
| 权重 | `0` 到 `1000`；同一模型和端点有多个可用账号时，较高权重优先。 |
| 用户名、密码 | 用于站点网页登录签到；不是 API Key 的替代品。编辑时密码留空表示沿用原值。 |
| 允许转发的端点 | 至少选择一个支持的端点。账号只会参与所选端点的请求路由。 |
| 可用模型和允许模型 | 先从当前 API Key 拉取模型目录，再选择允许转发的模型。修改 Base URL、站点类型、API Key 或高级参数后需重新发现模型。 |
| 启用账号 | 关闭后账号不参与请求路由，也不执行自动签到。 |
| 每日自动签到 | 仅在宿主定时任务运行时，对已启用且勾选此项的账号尝试签到。 |

模型目录从上游 `GET /v1/models` 获取。新账号保存前必须成功发现至少一个模型，并至少选择一个允许模型。

## 高级参数

“额外参数”必须是 JSON 对象。常用字段如下，未配置的字段使用插件默认值：

| 字段 | 作用 |
|---|---|
| `apiKeyHeader`、`apiKeyPrefix` | 指定模型发现和请求转发使用的 API Key 请求头及前缀。默认头为 `Authorization`，默认前缀为 `Bearer `。Anthropic Messages 默认使用 `x-api-key`，除非显式覆盖。 |
| `ReplaceHeaders` | 当前上游账号的转发请求头覆盖对象。头名不区分大小写，已有头替换、缺失头新增；在账号认证头和协议默认头之后应用。仅影响四种模型转发端点（含流式），不影响模型发现、额度、登录或签到。 |
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
| `POST` | `models/discover` | 用当前站点地址和 API Key 拉取可用模型目录；可传 `id` 读取现有账号参数。 |
| `POST` | `accounts/delete` | 按 `{ "id": "..." }` 删除账号。 |
| `POST` | `models/refresh` | 按 `{ "id": "..." }` 刷新账号可用模型目录。 |
| `POST` | `quota/refresh` | 按 `{ "id": "..." }` 查询并保存 NewAPI/Sub2API 额度快照。 |
| `POST` | `checkin/run` | 按 `{ "id": "..." }` 手动执行单个账号签到。 |

## 定时任务

插件注册任务 `forwardapi-daily-checkin`，Cron 为 `0 10 10 * * *`，即宿主任务时区每天 10:10 执行。任务仅处理启用且打开“每日自动签到”的账号，执行结果写入宿主任务日志。

## 实现位置

- `ForwardApiTerminal.cs`：平台、账号策略、模型快照、生命周期和定时任务注册。
- `ForwardApiTerminal.Requests.cs`：上游请求、原始响应/SSE 透传和签到执行。
- `ForwardApiTerminal.Accounts.cs`：账号管理、模型发现、额度查询和相关数据解析。
- `ForwardApiTerminal.Page.cs`：内嵌管理页面。

插件响应契约为 2.0。此插件只对显式配置的 OpenAI-compatible 上游使用 `RawContent` / `RawStream` 原样透传；透传响应不会被宿主重新序列化或 SDK 校验。其他内置插件必须返回共享的 `AdapterCompletion` / `StreamChunk`，由宿主按下游协议生成标准响应。
