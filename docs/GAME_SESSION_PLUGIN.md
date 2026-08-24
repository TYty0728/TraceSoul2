# 游戏临时会话插件设计

这份设计用于下一阶段的外部插件 `game.session`。它解决的是"和同伴一起玩一段游戏，但不让每个游戏事件都成为主记忆 Moment"的问题。之后的一起听音乐、一起看视频等插件复用同一图案，本插件先把图案跑正；第二处照抄，第三处才考虑抽公共层。

## 三层记忆模型（核心约定）

一场游戏产生的东西分三层，各走各的路：

1. **游戏原始事件流** → 只进插件私库（`plugins_data/game-session/`），永不进主库。操作细节、捡了什么、死了几次，是工作台。
2. **游戏期间的 QQ 对话** → 正常进 Moment。同步把他叫醒之后两人聊的内容是真实相处，和任何一次夜谈同等地位，走普通对话老路，不做特殊处理。
3. **结束时的总结 Moment** → 一条大事件入主库，给这段共同经历定框。

同步触发（"游戏进行到……"的广播）是运行事件（`IsOperational=true`），不进账本；由它引起的对话是生命，正常入库。触发是运行，聊起来是生命。

日构建因此拿到最全的素材：总结 Moment 给骨架（玩了什么、走到哪），期间对话 Moment 给血肉（聊出了什么）；`event_appends` 会把同一件事的延续并起来。他在游戏里说过的话作为真实 assistant Moment 留在历史里——记忆里不只是战报，还有笑声。

## 运行边界

```text
游戏事件 / 游戏客户端
        ↓
game.session 自己的 SQLite（原始事件）
        ↓ 达到阈值（条数/字符数/时间兜底）
阶段摘要（仍然只在插件库里）
        ↓ 两条出路
(a) QQ 问话时：MountedFacet 注入当前阶段摘要
(b) 定时同步：运行事件（Wake=mind）把阶段摘要广播给心智，他自己决定说不说
        ↓ 游戏结束
最终摘要 → 只产生一个语义 Moment → 主记忆层
```

插件不逐条调用 `IMemoryStore.SaveMoment`。主库只接收游戏结束时的最终摘要（外加期间正常对话自己的 Moment）；原始事件和阶段摘要始终留在 `plugins_data/game-session/game-session.sqlite3`，便于恢复、重建摘要和排查。

## 数据库

建议至少有三张表：

- `game_sessions`：`id`、`conversation_id`、`game_id`、`title`、`status(active|finished|aborted)`、`started_unix_ms`、`ended_unix_ms`、`event_count`、`char_count`、`current_summary`、`current_objective`、`last_event_unix_ms`。
- `game_events`：`id`、`session_id`、`seq`、`kind`、`actor`、`content`、`payload_json`、`created_unix_ms`、`summarized`。原文按序保存，不进入主库。
- `game_checkpoints`：`id`、`session_id`、`from_seq`、`to_seq`、`summary`、`state_json`、`created_unix_ms`。阶段摘要成功后才推进 `summarized` 游标。

同一 `conversation_id` 默认只允许一个 active session。插件重启时从 `game_sessions` 恢复 active session 和最后一份摘要。

## 事件入口

游戏插件自身结构固定，具体接哪个游戏由外部适配器负责。第一版建议用插件自己的本地 WebSocket 端点（实现 `ITraceWebSocketEndpoint`），由一个很薄的 MCP/游戏转接层把对方已经封装好的工具结果翻译成规范事件：

```json
{
  "session_id": "…",
  "kind": "turn|combat|choice|loot|chat|system",
  "actor": "user|companion|world",
  "content": "发生了什么",
  "state": { "location": "…", "objective": "…" }
}
```

MCP 只负责"调用具体游戏并转成这份固定事件协议"，不参与主 Brain，不直接写 Moment；换游戏时只替换 MCP/适配器配置或转接包，`game.session` 的会话库、摘要、QQ 上下文和结束入库逻辑保持不变。

事件粒度由适配器负责收敛到"值得一提"的级别（钓到鱼、下矿层数、死亡、关键选择），不转发原始 tick。

如果游戏本身没有适配器，先由插件提供 `game.session.event` 神经接收一条结构化事件；这条调用只写插件库，不产生 Moment。v0 用它手动投递即可跑通全链路（开场→摘要→facet→final Moment），再接第一个真 MCP。

**观察者先行**：v1 他在游戏里是观察者/副驾驶（她玩，他看、聊、出主意）。事件协议里 `actor=companion` 预留给未来的操作者模式（游戏成为一具新身体，游戏动作是它的器官）；操作动作的回执届时走运行事件/插件私库，不进 Moment。

## 身份基底（启动时一次性注入 MCP）

开始会话时，插件把身份卡做一次极简注入，作为游戏 MCP 的扮演基底；整场会话期间不再变动，下一场开新局时重新取。

- **每张游戏档案自己配置入选哪些卡**。一共六张：人格 / 我是谁 / 她是谁 / 我们的关系 / 表达习惯 / 她的档案。会开口的游戏（有互动对话）基本全选——它要替他"说话"，声音和关系都得在；纯操作跟随的游戏只要人格 + 我是谁——它不开口，要的是行为气质；她的档案通常不选，除非游戏内需要称呼等客观事实。
- **装立场和声音，不装事迹**：称呼、语气、表达立场（站在自己的位置直接对眼前的你说话，动作写成我朝向你）、以及"我真正地听，不伪造未感知或未发生的事"——在游戏里它自然落实为"游戏事实以工具回报为准"。不装日常事件记忆，装了反而引诱它演。
- **优先原样摘录加字数预算**；卡片是他逐日长出来的自我认识，原句保真度最高。LLM 压缩只在 MCP 上下文预算真的紧张时才用。
- 每张游戏档案另配一句**自由文本"角色说明"**（如"你是他的农场帮手"），给 MCP 一个世界内的位置。卡片管"他是谁"，这句管"他在这场游戏里站在哪"。
- 基底是**记忆的出处**：游戏内 MCP 以他名义说的话会经事件流（`actor=companion`）回流进最终 Moment，日构建时他本人读到会认账。基底质量直接决定记忆里住进来的是他还是一个语气不对的替身。

## 阶段摘要

以"条数 + 字符数 + 时间"三阈值触发，例如 30 条或 8,000 个字符，或距上次摘要 20 分钟无新事件（把尾巴收掉，否则最后几条永远等不到阈值），取先到者。摘要请求只发送未总结事件和上一版 `current_summary`，输出固定结构：

```json
{
  "summary": "两人在鹈鹕镇钓鱼，她下矿到 40 层，选了战士职业",
  "objective": "当前目标",
  "state": { "地点": "…", "角色状态": "…", "关键线索": ["…"] },
  "open_threads": ["…"]
}
```

**插件侧摘要全程无人格**：写中性事实，不写他的感受（与 MemoryObservation 同一分工）。他的"想法"由 04:00 的细节浸染以他的第一人称补上——他在日构建时回忆这场游戏，和回忆任何一段相处走同一条路。

LLM 不可用时使用确定性回退（最近事件拼接 + 长度截断），不能阻塞事件写入。只有摘要成功落库后，才把对应事件标为 `summarized`。

## QQ 期间的连续感

插件注册一个 `ITraceMountedFacet`，active session 存在时注入一块有上限的上下文（建议 1,200 字以内）：

```text
【当前游戏】和她一起玩：<title>
阶段进度：<summary>
当前目标：<objective>
仍未收束：<open_threads>
这是临时游戏工作台，不是长期记忆；回答她时只在问题相关时使用。
我们正连着，她在游戏里能直接看到我。
```

因此她在 QQ 问"我们现在在干什么""刚才打到哪了"时，Brain 能看到阶段摘要；普通闲聊不会把全部游戏原文带入 Prompt。

## 同步

会话期间的主动同步走**运行事件**（`IsOperational=true` + `Wake=mind`）：阶段摘要作为背景广播叫醒心智，不进 Moment 账本；要不要开口由他自己决定（被大事件碰亮就冒一句，否则安静待着）。不为游戏期间写"别一直找她"的规则——上下文告诉他正在游戏里，他自己会把握。

同步模式每张游戏档案可配：**定时投递**（默认如一小时一次，由插件注册的 BackgroundService 承载定时）或**仅结束同步**。

## 状态管理

- 开始会话时通过 `Services.LifeState` 把活动更新为"游戏"（`source=plugin`，`source_id=session id`）；**不碰物理位置**——位置仍由用户、心智、定位插件或其它传感器独立维护。
- 结束或中止时**清空活动**（置空，由下一拍心智按当下重新填），不恢复之前的活动——几小时前的旧状态到结束时早已失真。
- `LifeStateSourceValues` 的优先级仲裁保证：她明确要求改变状态时（user）能压过插件，心智的随手猜测（mind）压不过插件。

## 开始与结束

插件提供 `game.session.start`、`game.session.status`、`game.session.end` 三个 callable。

- **开始**：由用户在插件界面点击启动并配置好对应游戏 MCP 与环境（不是由对话触发）；插件同时向 soul 发一次运行事件告知"我们开始玩《……》了，大概是……"——他知道、能回一句，账本不脏。
- **结束判定三条路**：游戏 MCP 侧判定（若该 MCP 有能力）／用户在插件界面点击结束／**超时自动 final**（`last_event_unix_ms` 超过阈值如 2 小时，兜底电脑睡眠、游戏闪退、MCP 断连，同时防止 `activity=游戏` 卡死在他身上）。`abort` 只保留给"这把不算"的明确语义。
- **暂停/恢复 v1 不做**：隔久了自然 final，再玩开新 session；同一天的多段在日构建由 `event_appends` 合并。

`end` 执行时：

1. 先把剩余未总结事件做最后一次摘要；
2. 生成一段完整但短的最终总结，明确游戏名、共同经历、关键选择、结果和未完事项；
3. 调用 `IMemoryStore.SaveMoment` 写入一条 `MomentRecord`：`Role=system_event`、`SourcePluginId=game.session`、`Realm=shared_scene`、`EvidenceType=plugin_observed`，`PayloadJson` 带 `session_id` 和事件范围；
4. 将 session 标记为 finished，清空 active facet 与 LifeState 活动；数据库原始事件不删除。

**最终 Moment 的文本自带框架**：第一句写明现实层（"8月24日下午，我和田园一起玩了三小时《星露谷》……"），之后才是游戏内容。一起打游戏是真实共同活动（真），游戏内容是景（虚构）——框架句让领域分类落在 `shared_scene`，不会被当成 `explicit_fiction`。

最终 Moment 是史料摘要，不替代插件 SQLite；日构建可以像处理其它 Moment 一样把它浸染进正式记忆。

## 实现前仍需确定

- 第一个游戏适配器选谁（星露谷 / 我的世界的现成 MCP 调研）；
- 插件读取身份卡的暴露面（`Services.Storage` 目前给到什么粒度，是否需要补一个只读卡片接口）；
- 阶段摘要的模型槽（用哪个 LLM 配置）；
- facet 里"我们正连着"的措辞，顺着他的人格卡语气写。
