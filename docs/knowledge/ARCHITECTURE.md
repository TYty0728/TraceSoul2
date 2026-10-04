# 当前架构地图

## 整体结构

完整存在由**经历与认知的完整拼图〔主体自身、用户、世界、关系〕 + runtime + 衔接与运行逻辑**构成。以下文件和数据模块服务于这三个部分；身份卡是拼图的摘要视图，不是与拼图独立的自我。整体结构见 [完整存在架构](../EXISTENCE_ARCHITECTURE.md)，正式修订见 [架构总纲第 38 节](../ARCHITECTURE_ALIGNMENT.md#38-完整存在的三部分与人格拼图2026-09-29)。

当前源码已接通同源身份、连续主体、环境局部状态与昼夜拼图闭环。白天追加切片，深夜按来源范围整合经历与感受、形成认知及更新有依据的身份摘要。实现与离线验收见 [实施记录](../EXISTENCE_IMPLEMENTATION.md)，真实模型自然度未验收。

## 篇幅

模型要写下的正文，字数写在第一次指令里。超出这个篇幅时，按同一次任务、同一篇幅重新生成。不把超限文字截成短句，也不把它当成空结果丢掉，也不把超长草稿交回模型照抄。原先没有篇幅、写完再补一道缩短或凝练，不是这个闭环。日志预览和协议字段长度不在这条规则里。

今日轨迹、今日新识各自的阅读总长不超过1000字。超过时做一次筛选：去掉不重要的，精简留下的。原件保留，不截断。同一批内容只试这一次。页面和 runtime 不再列全部活跃事件。记忆页仍是昨天的日榜 10 条，以及本周、本月、本年、永久各 3 条。runtime 注入只带日榜 5 条，其余各 2 条。每一段的序号从 1 重新计，不沿用库里跨类的名次。升级时一次性删掉原先保存的活跃事件阅读稿。事件索引仍留给召回。当天回望不再按批数均分篇幅。某一段写超了先收下；接成整篇后，超过1000字才做一次精简，只要求把字数压到500字以内。切片很多时，第一次写作仍要求大多数不要出现。精简仍超限则不发表、不截断，已写各段保留。

## 一轮对话

```text
QQ WebSocket → OneBotPlatformAdapter.ConvertInbound → OneBot 收件箱
本机输入 → SoulRuntime.ChatAsync
后台事件 → SoulRuntime.PollBackgroundAsync
                         ↓ 同一运行锁
KernelLogic → 保存输入 / 判断唤醒类型 / 识图 / 记忆预激活
            → AgentLoopLogic（直接回复 / 行动后继续 / 等待）
            → 可选能力执行 → 结果返回同一 Agent 循环
            → ExpressorLogic 公共表达映射（仅 refine 才再次调用模型）
            → MouthLogic 身体路由 → 平台适配器
            → 文字与表情合并发送 / 追加当下切片 / 更新连续状态 / 安排下一次心跳
                         ↓
生图立即后台生成 → DeferredTurnWork → SoulRuntime 轮后队列
                → 取得运行锁提交发送与回执入库
```

普通对话一次 Agent 生成；工具/记忆续推、明确的表达加工、识图、画面规划与结构化纠正会增加请求。最多四轮行动后收口，模型调用次数不等于外发消息数。语音/动作可以独立执行，持续执行回执通过运行事件回流。见 [AGENT_HARNESS](../AGENT_HARNESS.md)。

## 主要文件

| 文件或目录 | 负责什么 |
|---|---|
| `Tools/Host/Program.cs` | WebUI/API、WebSocket、运行时生命周期与配置入口 |
| `Tools/Host/SoulRuntime.cs` | 单角色运行、运行锁、后台轮询、轮后提交队列 |
| `Tools/Host/TraceHome.cs` | 软件与家目录分离，角色/插件路径解析 |
| `Tools/Host/ExternalPluginLoader.cs` | 外部程序集加载与共享契约解析 |
| `src/TraceSoul2/Logic/KernelLogic.cs` | 主编排、唤醒分流、发送、回执和轮后任务 |
| `Logic/AgentLoopLogic.cs`、`Prompts/AgentLoopPrompts.cs` | 有界 Agent 循环、能力目录与同一主体续推 |
| `Logic/MindLogic.cs`、`Logic/ExpressorLogic.cs` | 兼容状态/上下文、公共表达映射与可选加工 |
| `Plugins/TraceExecutionRegistry.cs` | 持续执行、进度、取消请求与终态回执 |
| `Logic/CommonContextPackLogic.cs`、`Logic/LlmContextPackLogic.cs` | 公共前缀、历史窗口和供应商策略路由 |
| `Prompts/CorePrompts.cs`、插件的 `*Prompts.cs` | 核心与器官提示词；不要在路由层散落提示词 |
| `Logic/IdentityProjectionLogic.cs`、`Logic/PuzzleViewLogic.cs` | 身份来源、版本失效、分享批准及证据可见性 |
| `Logic/SubjectRuntimeLogic.cs`、`Logic/RuntimeContextLogic.cs` | 连续主体与局部状态的统一视图、版本提交 |
| `Logic/RuntimeSliceLogic.cs`、`Manager/SqliteRuntimeSlices.cs` | 白天追加切片、夜间日回望、来源保存与召回 |
| `Logic/PublicExperienceLogic.cs` | 公开原文与切片按环境进入夜间认知整理 |
| `Logic/MouthLogic.cs` | 身体/器官匹配与通道收口 |
| `Manager/TracePluginManager.cs` | 插件注册、启停、休眠、贡献目录和调用 |
| `Plugins/TracePluginContext.cs` | 服务、钩子、插件注册契约（编进 PluginApi） |
| `Plugins/Builtin/OneBotPlatformPlugin.cs` | QQ 连接、去重、收件箱、暂存、发送与输入状态 |
| `Plugins/Builtin/OneBotPlatformAdapter.cs` | OneBot 协议与规范消息互译 |
| `ExternalPlugins/QqImageGen/` | 相机提示、分享反馈、画面规划、参考图、生成和发图 |
| `Logic/HeartbeatLogic.cs`、`Plugins/Builtin/TimeSchedulerPlugin.cs` | 主动唤醒、睡眠与后续时间事件 |
| `Tools/Host/DailyPipelineWorker.cs`、`Tools/Migration/` | 日构建调度与处理管线 |

表中缩写 `Logic/`、`Plugins/`、`Prompts/`、`Manager/` 均相对于 `src/TraceSoul2/`。

## 数据与部署边界

长期拼图围绕他/user、世界/world、我/ass、我和他/relation 四领域关联；runtime 保留当下状态。`ContextRecallAdapter` 已通过 `MemoryRecallLogic` 接入预激活与按需查询，`SqliteCognitionGraph` 管理证据和修订，`CognitionFormationLogic` 校验日构建可见证据。身份由共同投影视图加载，夜间回望保留主观痕迹并参与有界召回，执行仍看真实设备回执。能力和预算见 [四领域拼图](../COGNITION_PUZZLE.md)。

- 主 SQLite：对话 Moment、运行事件、内心、身份、事实与索引等；向量 SQLite 由 `SqliteVectorManager` 管理。
- 图片、表情、调度等运行痕迹保存为 `OperationalEventRecord`，不混成真实聊天历史。相机反馈从这里读取成功发图回执。
- `MindLogic.Normalize` 当前清空普通轮的 `cognition`、`archive`；不要照旧交接文档恢复白天高频认知写入。
- PluginApi 是共享契约程序集，Host 与插件共享同一份类型；核心源清单显式维护。
- `TRACESOUL2_HOME` 放角色和全局配置，`TRACESOUL2_PLUGINS` 放代码包，`TRACESOUL2_PLUGINS_DATA` 放插件配置和图库。软件目录可以替换，角色与插件数据需要保留。
- 启动器、发布包和 Docker 可用不同路径。排障先确认实际启动脚本与进程，不从源码位置猜运行家目录。
