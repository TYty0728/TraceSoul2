# 游戏临时会话插件设计

这份设计对应已经落地的外部插件 `game.session`。它解决的是"和同伴一起玩一段游戏，但不让每个游戏事件都成为主记忆 Moment"的问题。之后的一起听音乐、一起看视频等插件复用同一图案，本插件先把图案跑正；第二处照抄，第三处才考虑抽公共层。

## 三层记忆模型（核心约定）

一场游戏产生的东西分三层，各走各的路：

1. **游戏原始事件流** → 只进插件私库（`plugins_data/game-session/`），永不进主库。操作细节、捡了什么、死了几次，是工作台。
2. **游戏期间的正常对话** → 正常进 Moment。同步把他叫醒之后两人聊的内容是真实相处，和任何一次夜谈同等地位，走当前平台的普通对话老路，不做特殊处理。
3. **结束时的总结 Moment** → 一条大事件入主库，给这段共同经历定框。

同步触发（"游戏进行到……"的广播）是运行事件（`IsOperational=true`），不进账本；由它引起的对话是生命，正常入库。触发是运行，聊起来是生命。

日构建因此拿到最全的素材：总结 Moment 给骨架（玩了什么、走到哪），期间对话 Moment 给血肉（聊出了什么）；`event_appends` 会把同一件事的延续并起来。他在游戏里说过的话作为真实 assistant Moment 留在历史里——记忆里不只是战报，还有笑声。

## 星露谷一键安装

WebUI 的「一起玩 → 星露谷物语 → 安装与连接」提供本机安装向导。点击「一键安装」后会：

1. 从 Steam 配置自动定位 `Stardew Valley.exe`；
2. 从 `Pathoschild/SMAPI` 官方 GitHub Release 下载 Windows 安装包，并按 Release API 提供的 SHA-256 摘要校验；
3. 用 SMAPI 安装器的 `--install --game-path ... --no-prompt` 参数安装到游戏目录；
4. 克隆 `amarisaster/StardewValley-MCP`，自动应用 TraceSoul 的单同伴/显示名兼容补丁，再执行 MCP Server 的 npm 安装与构建；
5. 设置 `GAME_PATH` 编译 SMAPI Mod，由 ModBuildConfig 部署到游戏的 `Mods` 目录；
6. 在插件数据目录生成 `stardew/mcp-connection.json`，保留 MCP Server、桥接 JSON 与动作队列的绝对路径。

安装动作不会读取或修改存档，也不会直接修改 Steam 配置。完成后页面会提供可复制的 Steam 启动选项，或直接用 `StardewModdingAPI.exe` 启动游戏。真正的桥接状态需要进入游戏并加载一个存档后才会出现。

## 运行边界

```text
游戏事件 / 游戏客户端
        ↓
game.session 自己的 SQLite（原始事件）
        ↓ 达到阈值（条数/字符数/时间兜底）
阶段摘要（仍然只在插件库里）
        ↓ 两条出路
(a) 任意对话平台问话时：MountedFacet 注入当前阶段摘要
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

MCP 只负责"调用具体游戏并转成这份固定事件协议"，不参与主 Brain，不直接写 Moment；换游戏时只替换 MCP/适配器配置或转接包，`game.session` 的会话库、摘要、对话上下文和结束入库逻辑保持不变。

事件粒度由适配器负责收敛到"值得一提"的级别（钓到鱼、下矿层数、死亡、关键选择），不转发原始 tick。

如果游戏本身没有适配器，先由插件提供 `game.session.event` 神经接收一条结构化事件；这条调用只写插件库，不产生 Moment。v0 用它手动投递即可跑通全链路（开场→摘要→facet→final Moment），再接第一个真 MCP。

**玩家 2 默认**：星露谷档案默认选择 Player 模式，自动绑定 Soul 的 `identity_base` 与专用本地 Ollama；游戏 Agent 逐步调用移动、工具、交互与战斗能力。Follow 仍可手动选择，此时只使用 Mod 状态机而不调用 LLM。操作回执只进运行事件/插件私库，不逐条写 Moment。

## 身份基底（启动时一次性交给游戏 Agent）

开始会话时，插件把身份卡做一次极简注入，作为调用 MCP 的游戏 Agent 的扮演基底；整场会话期间不再变动，下一场开新局时重新取。MCP Server 本身只是工具服务器，不消费人格提示词，不能把 `identity_base` 直接“塞给 MCP”。

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

## 对话期间的连续感

插件注册一个 `ITraceMountedFacet`，active session 存在时注入一块有上限的上下文（建议 1,200 字以内）：

```text
【当前游戏】和她一起玩：<title>
阶段进度：<summary>
当前目标：<objective>
仍未收束：<open_threads>
这是临时游戏工作台，不是长期记忆；回答她时只在问题相关时使用。
我们正连着，她在游戏里能直接看到我。
```

因此她从 QQ、其它聊天平台或独立壳问"我们现在在干什么""刚才打到哪了"时，Brain 都能看到阶段摘要；普通闲聊不会把全部游戏原文带入 Prompt。

## 同步

会话期间的主动同步走**运行事件**（`IsOperational=true` + `Wake=mind`）：阶段摘要作为背景广播叫醒心智，不进 Moment 账本；要不要开口由他自己决定（被大事件碰亮就冒一句，否则安静待着）。不为游戏期间写"别一直找她"的规则——上下文告诉他正在游戏里，他自己会把握。

同步模式每张游戏档案可配：**定时投递**（默认如一小时一次，由插件注册的 BackgroundService 承载定时）或**仅结束同步**。

## 状态管理

- 开始会话时通过 `Services.LifeState` 把活动更新为"游戏"（`source=plugin`，`source_id=session id`）；**不碰物理位置**——位置仍由用户、心智、定位插件或其它传感器独立维护。
- 结束或中止时**清空活动**（置空，由下一拍心智按当下重新填），不恢复之前的活动——几小时前的旧状态到结束时早已失真。
- `LifeStateSourceValues` 的优先级仲裁保证：她明确要求改变状态时（user）能压过插件，心智的随手猜测（mind）压不过插件。

## 开始与结束

插件提供 `game.session.start`、`game.session.status`、`game.session.end` 三个 callable。

- **开始**：由用户在插件界面点击启动并配置好对应游戏 MCP 与环境（不是由对话触发）。星露谷会话先启动 stdio MCP、调用 `stardew_spawn`、设置所选模式，并等待 `bridge_data.json` 真正出现目标同伴；只有游戏侧确认后才向 Soul 发“已经进入”的运行事件。超时或桥接离线会让开始操作失败，不允许语言反应冒充游戏回执。
- **结束判定三条路**：游戏 MCP 侧判定（若该 MCP 有能力）／用户在插件界面点击结束／**超时自动 final**（`last_event_unix_ms` 超过阈值如 2 小时，兜底电脑睡眠、游戏闪退、MCP 断连，同时防止 `activity=游戏` 卡死在他身上）。`abort` 只保留给"这把不算"的明确语义。
- **暂停/恢复 v1 不做**：隔久了自然 final，再玩开新 session；同一天的多段在日构建由 `event_appends` 合并。

`end` 执行时：

1. 先把剩余未总结事件做最后一次摘要；
2. 生成一段完整但短的最终总结，明确游戏名、共同经历、关键选择、结果和未完事项；
3. 返回或投递一条语义 `PluginEventData`，由 Kernel 在宿主运行锁内写成唯一一条 `MomentRecord`：文本先明确“已经从游戏里出来、回到平常的相处场景”，再记录共同经历；`Role=system_event`、`SourcePluginId=game.session`、`Realm=shared_scene`、`EvidenceType=plugin_observed`，`PayloadJson` 带 `session_id`、事件范围和 `transition=left_game`；
4. 将 session 标记为 finished，清空 active facet 与 LifeState 活动；数据库原始事件不删除。

**最终 Moment 的文本自带框架**：第一句明确“我们已经结束游戏，从游戏里出来，回到平常的相处场景”，随后写明现实层（"8月24日下午，我和田园一起玩了三小时《星露谷》……"），之后才是游戏内容。一起打游戏是真实共同活动（真），游戏内容是景（虚构）——框架句让领域分类落在 `shared_scene`，不会被当成 `explicit_fiction`，Soul 也能明确知道当前已经不在游戏中。

最终 Moment 是史料摘要，不替代插件 SQLite；日构建可以像处理其它 Moment 一样把它浸染进正式记忆。

## 通用层当前落点

- 外部插件源码：`ExternalPlugins/GameSession/`；插件 id 为 `game.session`，不声明 `PlatformId` 或 QQ 身体。
- 控制台侧栏有独立的“一起玩”一级入口；插件卡片可直接跳转。进入后先显示游戏库，选中具体游戏才进入它的二级工作台。星露谷工作台可选择同伴/Player 2 模式、MCP 目录和游戏 Agent，显示会话、链路、阶段摘要、最新状态和实时事件，并可正常结束或中止会话。
- 固定桥接入口：`ITraceWebSocketEndpoint` + `game.session.v1`，协议与事件 JSON Schema 在插件的 `protocol/` 目录。
- 私库：`plugins_data/game-session/game-session.sqlite3`，三张表与本文一致。
- 摘要模型：优先复盘槽 `ReviewLlm`，其次当前轮 `Llm`；不可用或失败时确定性回退。
- 身份卡读取面：现有 `Services.Storage.LoadIdentityCards` 已足够，不需要新增主库写权限。
- WebSocket 端点已纳入插件正式生命周期；重扫会移除旧端点，新握手按路径解析当前插件实例。

## 第一个适配器：Stardew MCP Bridge

第一目标是社区项目 [`amarisaster/StardewValley-MCP`](https://github.com/amarisaster/StardewValley-MCP) v0.3。它不是 ConcernedApe / 游戏官方项目；仓库实现由 Node stdio MCP Server、JSON 文件桥和 SMAPI Mod 三段组成。

- `bridge_data.json` 约每 0.5 秒刷新，包含时间、季节、天气、玩家和同伴状态；Player 模式还带周围地块、背包与 `lastCommandResult`。
- MCP 动作工具返回的 `Command sent.` 只说明动作文件已入队，不能直接翻成“已经完成”。适配器必须等待后续状态变化或 `lastCommandResult.success/detail` 再生成事实事件。
- Follow 模式由 SMAPI Mod 自主执行，不调用 LLM；Player 模式由插件内游戏 Agent 消费 `identity_base` 和实际同伴状态，每次只规划一个短动作，再通过 MCP 工具执行。
- 游戏 Agent 默认优先使用本地 Ollama（OpenAI 兼容地址 `http://127.0.0.1:11434/v1`）。它通过宿主 `ILlmProviderDirectory.CreateClient` 创建隔离客户端，不读取供应商配置文件、不复制密钥，也不替换 Soul 的主对话模型。会话环境记录 `agent_provider_id`、`agent_model`、`sensor_poll_ms` 和 `decision_interval_ms`。
- 适配器只上报有意义的状态差异：地点/日期推进、模式切换、关键劳作结果、矿层、战斗、倒下、聊天与重要交互；逐帧位置和重复快照丢弃。
- stdio MCP 进程与游戏会话同生命周期；结束时把同伴切到 idle 后关闭客户端，异常退出会由后台会话检查自动重连。WebUI 的绿色连接状态必须同时满足桥接刷新和 MCP 进程在线。
- 上游 `stardew_spawn` 原本固定生成 `Companion1` 与 `Companion2`；安装器会应用一个可重复检查的兼容补丁，让它只生成 WebUI 选中的内部角色。内部 ID 仍保持 `Companion1/2` 供工具稳定寻址，游戏显示名默认取当前 Soul 名字，也可在开局前覆盖。
- WebUI 默认使用星露谷原生 `Farmer` 分层角色，可以直接填写性别模板、肤色、发型、上衣、裤子和配饰 ID，不需要准备图片；游戏内对话头像仍沿用 Mod 默认素材。也可切换到 NPC 精灵模式，分别上传当前所选同伴的行走精灵与对话头像 PNG。行走精灵必须是 `64 × 128`，单张不超过 6 MB；素材同时保存到插件私有目录和 Mod 资源目录，并在下次完整启动游戏时加载。
