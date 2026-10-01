# Agent 注入来源与精修清单

## 2026-10-01 最新样本复查与表达修订（源码，尚未发布）

- 只读核对两天 dump：9 月 30 日有两次生图尝试，其中中午那次包含一份 `finish + actions` 冲突输出及纠正，不能算两次拍照；最近一次调用在 23:39。10 月 1 日 04:52～13:34 共 21 次 Agent 输出（15 次 finish、6 次 wait），没有 actions；04:47 的另一份自由文本属于夜间余温，不计入 Agent。最新请求已有相机能力、主动分享说明和真实发送反馈（约 831 分钟、21 次文字回应），因此本样本的问题在模型未选择图片，不能归为能力未注册或图片发送失败。回执仍不等于客户端显示验收。
- 本地保留的旧 AstrBot 相机源码把拍照说明追加到 system，模型在回复中选择写出 `[photo:…]` 后由插件处理。没有按轮数强制生成；其中“请务必”要求使用拍照格式，不能等同于用户要求每轮发图。该副本不证明最初安装版本的全部配置。当前 Agent 需要 `continue + actions`，普通 reply/scene 描写不触发图片；调用分支、提示位置和上下文差异可能影响选择，不能仅凭样本确认为单一根因。
- 相机目录现明确角色照片可取材于共同文字场景，并给出符合当前协议的调用示例，说明画面描写不等于发图、确认成功后再收尾；动态反馈取消“需要专心听话即可不拍”的宽泛抑制，保留明确拒图/安静约定和自主选择。无固定配额、无关键词补图、不改变状态机或增加固定模型调用。
- 中英文混杂有两条可核查来源：11 份事件观察的 `event_decision` 含英文内部术语，经 DayBuilder 映射为 `fact_decision`，由 MigrationDb 写入观察记录；召回适配器也直接拼接英文关系/场景/证据类型。普通最新 Agent 状态未发现同类英文术语混写，不能把正常产品名或原文专名当作故障。
- 修订只约束新生成正文用自然中文，覆盖状态/目标、事件说明、细节、认知和身份摘要；协议键、枚举、ID 及原文专名保留。认知召回展示将已知标签译为中文，支持与反证、关联依据和原始 ID 保持区分；存储协议及旧正文不改写，不加正则删词或额外润色调用。模型生成自然度仍需真实使用验证。

验证：完整 ChatCheck、Prompt 布局与实际 Migration 契约入口；Host、ChatCheck、相机插件和 Migration 构建；差异检查。均使用模拟模型或只读日志，不访问真实聊天模型或发送 QQ。源码未发布、未替换运行实例、未清洗已有数据库。

核对日期：2026-09-30；源码基线 v0.1.21。对照用户提供的全天 162 份请求及响应，仅记录结构、字段和聚合统计，不复制原始对话、账号、身份卡正文或工具查询。先完成状态完整性修复（0.1.22），表达与相机留到下一项单独精修。

## 请求外层顺序

统一装配入口为 `CommonContextPackLogic.Assemble`，普通对话由 `AgentLoopLogic.RunAsync` 调用：

1. 一条 system：身份视图。
2. 对话历史：按窗口选取并映射为 user/assistant。
3. 一条 user：Agent 稳定规则、表达姿态、输出类型。
4. 一条 user：相关记忆。
5. 一条 user：本轮动态上下文、能力与结果。
6. 一条 user：当前真实发言。后台事件改放动态段，不冒充用户发言。

因此只有身份是 system；框架规则、相关记忆和动态段目前都以 user 消息承载。类型错误纠正还会临时追加一条纠正要求。

## 可逐项精修的注入单元

| 编号 | 单元 | 拼接来源 | 原始数据来源/触发 |
|---|---|---|---|
| I01 | 身份、人格、对方资料、关系 | IdentityProjectionLogic.Build → CommonContextPackLogic.SharedSystem | 身份卡、有效认知及其版本依据、人工固定与共享批准 |
| I02 | 最近真实对话 | KernelLogic.LoadAlignedDialogueHistory → CommonContextPackLogic.BuildRecentChatHistory | 当前环境的 Moment；排除运行事件；量化滑动窗口 |
| I03 | Agent 行为与表达规则 | AgentLoopLogic.BuildStable | AgentLoopPrompts.Rules、SubjectContinuity、ExpressionChoice；CorePrompts.Expressor.ExpressionPosture |
| I04 | 输出字段与类型 | AgentOutputContractLogic.BuildPrompt | AgentOutputData 及行动/目标等 DTO 反射；类型说明不等于字段用途说明 |
| I05 | 相关记忆、长期拼图 | MemoryRecallLogic.Preview、ContextRecallAdapter | 原始经历索引、认知节点、关联、当前关注与场景；主动 memory.recall 结果另进入 I12 |
| I06 | 联网规则 | AgentLoopLogic.RunAsync | 目录具有 web.search/read 时加入 AgentLoopPrompts.Search |
| I07 | 当前状态 | RuntimeContextLogic.State | time.context；本轮环境；LifeState；SubjectRuntimeLogic.View；近期交流间隔；今日新识；当天轨迹 |
| I08 | 有效偏好、约定、目标 | GoalMemoryLogic.BuildContext | 生效范围/时间过滤后的持久记录，包含依据与修订约束 |
| I09 | 插件观察、相机反馈 | AgentPromptContextLogic.Context | 非协议/非已归属的 ContextBlocks；私密环境的 MindTurnPromptAppends；CameraSharingContext |
| I10 | 本轮触发原因 | RuntimeContextLogic.Trigger | 人类发言、旁听、心跳或其他运行事件；不是额外真实聊天 |
| I11 | 可用能力目录 | TracePluginManager → AgentLoopLogic.BuildCatalog → AgentPromptContextLogic.Catalog | 启用插件、可用性、身体与环境过滤；Description/参数/适用/边界 |
| I12 | 执行、工具结果、状态增量 | AgentPromptContextLogic.Executions/Results/PendingState | 当前执行账本、实际行动回执、AgentTurnStateLogic；有内容才追加 |
| I13 | 表达受理、预算与特殊回执 | AgentLoopLogic.RunAsync | 本轮是否已受理表达；最后行动轮的预算限制；后台事件与设备回执 |
| I14 | 当前发言或格式纠正 | CommonContextPackLogic.Assemble；DeepSeekStructuredOutputLogic | 真实当前消息；仅解析/校验失败时追加纠正 |

## 全天补充核对与第一项修复

162 次请求中识别到 98 份含 step 的 JSON（含尚未通过状态机校验的输出）：today 20 份、mood 24 份、attention 4 份；new_fact 与 mood_changed 均为零。旧 today 覆盖保存，晚间简短更新能替换整段白天摘要；mood 写回依赖缺失的布尔标记。

中午有一组生图尝试：先 finish + actions 冲突，再纠正为 continue，随后进入相机规划请求。它们属于一次尝试和一次纠正。后续 Agent 请求携带 qq.imagegen.generate 的 success/已发送回执，因此不能说全天没有发图尝试，也不能仅凭插件回执证明 QQ 客户端实际展示。先前最新六份样本均未选图，不能推广为全天没有尝试；后续继续核对实际目标与平台回执。

0.1.22 将状态字段存在性带到写回及持久快照，省略 attention 保留原始年龄，明确空值清除；mood 新值直接生效。页面与 Agent 共用主体视图，展示真实 step、情绪、变化、新识、目标及执行回执，不补造旧 note/cognition。

今日轨迹从单行滚动摘要改为带真实来源、环境与时间的追加记录，读取时恢复旧 runtime_slices/turn_reviews 的 today，保留遗留摘要；重放幂等，原件不因日终退出视图而删除。实时注入、页面、闲暇上下文、夜间取材与日构建均接到同一读取入口。今日新识现展示完整当日条目，并明确 new_fact 的用途和依据；没有实际新信息时仍可省略。

## 先前最新六份样本的结构证据

六次请求包含四次初始 Agent 判断、一次格式纠正、一次 web.search 后续推。所有请求均识别为私密环境，均具备 qq.imagegen.generate、qq.image.send 和相机动态说明；没有调用图片能力。

最新请求内容合计 16,659 字符（不是 token 数）：身份 2,240；历史九条共 611；稳定规则/类型 3,151；相关记忆 1,976；动态段 8,650；当前发言 31。动态段内，能力目录约 2,015，偏好/目标约 1,327，工具结果约 4,574，相机反馈约 162。首份样本历史为十五条，后续窗口发生切换；只凭 dump 不能断言配置是否改变。

有四份有效 finish 输出、一份纠正后的 continue 输出、一份未形成 JSON 对象。有效输出均没有 attention/new_fact；其中有些仅有 step/reply，另有少量 inner/mood/scene/activity/affect。没有润色或画面规划调用。

## 初查发现（前四项已在 0.1.22 修正）

- 浮动关注：Agent 允许按变化省略字段，但 MindLogic.Normalize 将缺失 attention 化为空串；普通对话 MergePrivateFacets 传入 settleOldFragments=true，InnerLifeLogic.ProposeFromMind 会给出空关注写集，进一步清除已有碎片。这是写回行为，不应只靠要求模型每轮重填来补救。
- 控制台判断：PublicMindDecision 与 renderLive 仍展示旧 beat/note/tags/query/cognition/image 等字段；其中多项已不在 AgentOutputData 内。新 step/actions/goal_updates/affect 等又未在相应卡片中完整映射。空白不等于程序本轮没有判断，也不能把旧标签强填回来冒充模型输出。
- 今日新识：new_fact → MergePrivateFacets → memory.today.new → AddTodayNewItems 的入口仍在。当前提示只给 new_fact 类型，没有旧提示中“一条当天新事实/证据/时效”的语义解释；样本没有生成该字段。不能据此判断数据库历史便签被删除；需在修订时用独立临时库验证完整写入及显示。
- 情绪：mood_changed 目前只有类型定义，却是写回 mood 的必要条件；样本存在 mood 而没有该标记，不能保证相应情绪进入持久状态。模型字段、写回条件和页面展示需一并统一。
- 照片：这组日志能排除未绑定导致公开环境、目录隐藏、相机动态提示缺失；没有生成调用，无法判断图片服务自身是否可用。应精修表达意图、可见内容与能力选择的关系，避免继续叠加固定发图提醒或配额。
- 表情：FailureProtection.ObserveTurnFailures 把插件 failed 统一报警；旧 FailureProtectionChecks 还使用“未匹配”作为 failed/WARNING 示例。未命中应该是正常 skipped/empty，真正传输/服务异常才是 failed。当前仓库未检索到 qq.sticker 插件实现，不应把关闭所有真实错误报告当成修复。

## 建议精修顺序

先 I07 + I04（状态含义、字段省略/显式清空、写回与页面一致），随后 I03 + I09 + I11（表达选择与相机），再逐项整理 I01/I02/I05/I08/I12。每次选一个主题，同时对照实际注入、模型输出、代码写回和页面显示；不以增加命令句替代断裂的程序链。

电脑能力可作为后续独立身体/工具插件：主体提出意图，执行器观察、行动并返回可验证结果，仍回到现有 Agent/runtime；先明确专用工作目录、浏览器与可用应用，再考虑完整桌面。当前四轮行动预算与以文本为主的结果视图不能直接承载长时间桌面操作，需要独立持续任务及图像观察通道。
