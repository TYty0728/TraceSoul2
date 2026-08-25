# game.session v1 桥接协议

`game.session` 不认识星露谷、Minecraft 或任何具体 MCP。每个游戏只需要一个很薄的翻译器：调用它自己的 MCP，再把值得记录的结果翻成这里的固定 WebSocket 消息。

默认入口是 `ws://127.0.0.1:<host-port>/plugins/game-session/ws`。配置了 Token 时使用 `Authorization: Bearer <token>`，也可用 `?access_token=<token>`。

## 生命周期

1. 翻译器发送 `start`。返回值中的 `identity_base` 只在本局开始时交给调用 MCP 的游戏 Agent，后续不要反复拼接；MCP Server 自身只是工具服务，不消费身份提示。`session_id` 用于本局所有事件。
2. 每次有“值得一提”的工具结果时发送 `event`。不要转发 tick、逐帧坐标或无意义日志。
3. 可随时发送 `status` 读取当前阶段摘要。
4. WebUI 可发送 `history` 读取当前会话最近的规范事件与阶段 checkpoint。
5. 正常结束发送 `end`；明确“这把不算”才发送 `abort`。断连不等于结束，插件会在超时后自动 final。

请求和响应都可带 `request_id`。响应统一为：

```json
{ "ok": true, "request_id": "42", "op": "event", "data": {} }
```

失败时 `ok=false` 且带 `error`。

## 开始

```json
{
  "op": "start",
  "request_id": "1",
  "conversation_id": "tracesoul2",
  "profile_id": "stardew",
  "game_id": "stardew-valley",
  "title": "星露谷物语",
  "adapter_id": "mcp.stardew.v1",
  "environment": { "save": "Farm_01" }
}
```

`profile_id` 选择插件配置中的游戏档案。返回的 `identity_base` 与 `role_instruction` 是这局游戏 Agent 的静态基底。

## 状态与历史

```json
{ "op": "status", "conversation_id": "tracesoul2", "session_id": "..." }
```

```json
{ "op": "history", "conversation_id": "tracesoul2", "session_id": "...", "take": 50 }
```

`history.take` 默认 40，范围 1–100。返回最近事件（按 `seq` 正序）和最近 8 个 checkpoint；它是本地工作台读取面，不会把原始事件写进 Soul 主库。

## 事件

```json
{
  "op": "event",
  "request_id": "2",
  "session_id": "...",
  "kind": "choice",
  "actor": "user",
  "content": "在矿井 40 层选择了战士职业",
  "payload": { "tool": "choose_profession", "raw_id": "evt-108" },
  "state": { "location": "矿井 40 层", "objective": "回农场整理背包" },
  "occurred_unix_ms": 1787568000000
}
```

- `kind` 是开放字符串；通用建议为 `turn|combat|choice|loot|chat|system`。
- `actor` 固定为 `user|companion|world`。
- `content` 必须是已经发生的事实，不是待执行指令，也不是模型猜测。
- `payload` 保存翻译器排错所需的原始引用；`state` 保存当前可覆盖状态。二者都只进插件私库。

## 翻译器边界

每个具体 MCP 翻译器只做三件事：

1. 把 MCP 的工具名、参数和返回结构映射成稳定的 `kind/actor/content/state`；
2. 合并噪声，把事件粒度收敛到“稍后值得讲一句”；
3. 保留 `payload.tool` 与原始事件 ID，方便回查。

它不读取 Soul 主库、不写 Moment、不做人格摘要，也不决定是否在聊天平台开口。身份基底、阶段摘要、同步、facet 与结束记忆都由 `game.session` 统一承担。

为一个新 MCP 开适配器时，先用同一组录制工具结果做表驱动测试：输入 MCP response，断言输出的规范事件。第一批适配器确定后再抽共享 SDK，避免在还没看见差异前过早统一。
