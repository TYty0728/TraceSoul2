# 统一 Agent 切换的能力继承审计

2026-11-03。按用户要求扩大检查9月26日统一Agent循环是否遗漏旧能力。初次审计比较`771a165`（`6070bab^`）、`6070bab`（0.1.15）及`d1db6d7`加本地主题恢复改动；下方审计对照表保留修复前状态。随后按用户要求完成本地接回，最新状态如下。

## 同日恢复实现

- 主题导航与四步复盘生长已接回，见[专项](COGNITION_HIERARCHY_AUDIT.md#恢复实现)。保留事件、认知和原件的分工及多父主题归属，不恢复已取消的白天归档或宣称任意深度树已经实现。
- `AgentLoopLogic` 实际调用 `MindTemplateLogic.SelectAsync`，按当前输入语义选择最多2项情境提示，使用适配当前行动、目标和复盘契约的 `FormatForAgent`。不增加生成模型阶段，不恢复固定等待台词或搜索即身体离场。
- `ToolExperienceLogic` 接回工具读取后的连续记忆。插件可显式声明 `RetainResultAsObservation`；成功、非外部副作用的读取结果保留为带来源的 `PluginObserved` 原件。插件已有 `ProducedEvent` 优先使用且只存一次；失败、空结果、仅受理/进行中不自动制造观察。QQ空间、联网与媒体理解已接入。
- 原件由现有 Moment 持久化，保留父输入、插件、受众及引用。后续24小时动态上下文最多展示3份近期原件的450字符节选；不依赖模型填写 `new_fact`。无主题限定的召回可从同会话最近2000份工具原件按词项匹配最多3份650字符节选（这里不是BGE语义检索），`memory.source` 可按4000字符分页读原文；所选主题仍只读已归类的事件与认知。
- 工具原件进入现行四步日复盘，事件与认知保留观察原件依据；工具说了什么不自动变成已核实事实。没有新增每日必跑模型阶段、数据库表或历史回填。
- 页面和运行计时的 `Brain 思考中/整轮`、`BrainFrame 挂载`、`Facet 写回` 等改为当前语义；页面阶段来自Agent实际step。移除孤立的白天归档私有入口，旧插件提示槽明确标注兼容用途。数据库文件名、共享类型和既有JSON字段保留，避免为术语清理破坏兼容。

验证：完整ChatCheck及Prompt布局通过；实际Migration回归通过，覆盖跨天主题生长、工具观察进入事件与认知来源、检查点恢复及事务回滚。真实Kernel测试覆盖情境进入请求、同轮去重、遗漏new_fact的下一轮读取、数据库重开、长原件尾部召回/完整分页、失败状态和受众隔离。Host及受影响插件构建通过。测试使用模拟模型/平台与临时库，未运行真实模型或改正式数据；本地源码尚未发布部署，不能据此认定真实模型归类质量已验收。

## 结论与范围

### 日期与主题召回复核

后续状态：用户要求设计并开始实施后，已在本地增加范围读取契约与执行过滤，见[当前实现](../MEMORY_READ_ROUTING.md)。下方保留修复前证据；探针现使用period验证仅昨日候选。精确发生时间与记录时间的区别仍是明确限制，不能据本版宣称所有回顾性叙述已按发生日索引。

2026-11-03 用户进一步提出“昨天我洗头了吗”和“我喜欢吃什么”。本地实际调用链复核发现，前述恢复仍缺时间定位，不能宣称完整恢复所有检索范围机制。

- 当前 `AgentLoopLogic.BuildCatalog` 的 `memory.recall` 只声明query、tags。Kernel把query和选定主题传到 `MemoryRecallLogic.Assemble`；其 `GetEventIndexesByFilter(conceptIds, null, null, null, null, null, null, 500)` 没有日期条件。BGE只负责在传入候选中排序，不解析“昨天”为日期范围。
- `ChatCheck --memory-routing-audit` 使用临时SQLite、模拟Agent和捕获候选的检索器，经真实Kernel处理“昨天我洗头了吗”：今天、昨天、八天前以及饮食事件均进入检索候选。选“日常清洁”主题也只有主题过滤，没有按日期收窄。这证明范围缺失，不代表已测得真实模型必然回答错误。
- 同一探针对“我喜欢吃什么”显式选择“口味偏好”，检索候选仅剩饮食条目，清洁事件排除。既有主题回归另覆盖认知过滤。主题候选按语义选出，是否调用、选择哪个主题仍由Agent决定；本次没有调用真实模型验证自主选择。有效goal偏好还可能直接进入上下文，回答正确也不证明触发了主题查询。
- 旧 `MemoryNervePlugin.ActivateMemoryNerve` 保留子代理多维定位：概念、时段、月份、地点、人物、心情，再在范围内做语义召回。`ToolLookupLogic.IsLookupEligible` 排除memory前缀，Agent仅手动加回recall/topics/source，实际目录不含旧activate。
- 旧时间字段是时段标签与yyyy-MM月份桶；数据注释明确时段不含今天/昨天。该定位契约和存储过滤未见开始/结束日期。“昨天”榜单属于固定上下文选录，时间工具的相对日期是展示转换，都不能替代指定日范围查证。`6070bab^` 的 `MemoryRecallLogic.Assemble` 同样未传时间条件，不把精确按日缺口全部归因9月26日。

后续修复需补“问题范围→日期/主题约束→范围内检索”的当前Agent契约，并覆盖无结果不跨日兜底、时间与主题组合、日界线/时区、原件及认知来源的范围语义；预激活与工具观察旁路也应核对，不能只给query加一句提示。此次只新增离线探针与文档，未修改产品召回、历史库、线上实例或验证真实模型分类质量。

切换并非纯粹把两个模型调用合成一个：它替换了调度入口，也改变了哪些提示、状态和副作用由程序保证。确认旧主动选主题入口被切断（已在本地恢复）；此外，**情境模板选择未进入新主链、普通工具结果自动写今日新识的桥接被删除**。后者有插件事件及Agent显式`new_fact`两种局部替代，不能说所有工具结果都丢失。

同时发现可追溯的历史退化和后续修复：行动中状态/心跳计划继承、自动表情、自动配图。它们发生在不同提交，不能统称9月26日一次删掉，也不能把今天已经修好的项目列成待修故障。

审计覆盖旧主链全部删除的方法、旧Mind动态上下文、当前Agent契约/目录/续推、表达映射、心跳/空闲/梦话、记忆与工具结果落点、后续修订和测试入口。未检查生产启用的插件组合、模型请求、用户历史聊天或线上版本；不据源码判断真实模型情感表现和分类质量。

## 能力对照表

| 能力 | 切换前及变化 | 当前归宿 / 判断 |
|---|---|---|
| 主动选语义主题再召回 | 旧Mind列候选并传tags；6070bab禁用旧候选，memory.recall只传query | **确有断链，本地已恢复**。主题恢复见[专项](COGNITION_HIERARCHY_AUDIT.md#恢复实现)，尚未发布 |
| 情境提示选择 | MindTemplateLogic按当前句语义选择最多2个“翻旧事/当场做完/短商量/接住/出门/记下/放下”模板 | **旧机制未接回**。Agent通用Behavior包含部分原则，但没有此检索与注入流程 |
| 工具结果进入今日新识 | TryRunMindToolCallAsync调用RememberMindToolPayload写短便签；两个方法在6070bab一起删除 | **部分替代，连续性存在缺口**。ProducedEvent会保存，Agent的new_fact可保存；只返回Payload的工具可能仅留审查快照 |
| 工具语义预选 | ToolLookupLogic.SelectAsync最多选择2项长尾能力 | **换成全量可用目录**，不是不能用工具。当前没有同等数量/字符总预算，属于随插件数量增长的风险 |
| 查询空间的自动前置调用 | TryReadQzoneIfAskedAsync在模型前根据词面触发 | **迁移为Agent显式行动**，qq.qzone.read仍在目录；不再保证某句话触发自动读取 |
| 固定“出门”流程 | 先说等待、执行工具、再次Mind判断，并临时改身体为out | **被行动续推替代**。工具反馈后继续判断保留；固定等待台词及搜索即物理离场没有保留，不宜照搬恢复 |
| 自动表情 | 9月27日8fa80fb的直接回复分支清附件 | **9月28日c13c0a8已修复**：正文后按语境匹配表情；不是9月26日直接删除且至今未修 |
| 自发配图 | 后续表达选择调整后主要依赖Agent主动选相机 | **10月2日67bef2f改为程序建立机会＋相机语义判断**，显式图片行动仍在；不是图片后端不存在 |
| 行动前状态、后续联系计划 | 初版循环把最终step交给Kernel，早先步骤只有历史文本，没有程序合并 | **9月30日b6c0437已修复**。AgentTurnStateLogic明确继承省略字段，最终统一提交 |
| 发消息失败后的状态/计划 | 旧流程及初版Agent容易让发送异常阻断轮后提交 | **后续已修复**：状态与下次唤醒在最终发送前提交；不是当前仍依赖成功发送 |
| 心跳开口判断 | 6070bab移除ApplyHeartbeatSpeakDecision旧启发式 | **行为修订**。10月26日22d171b建立Agent同次heartbeat_intent门控，查询可先做、无意图不外发 |
| 心跳存续、sleep | 调度入口仍在；模型失败后存续曾需要补强 | **保留并加强**。SyncHeartbeatAsync、时间服务及9月30日持久后备唤醒；wait不等于sleep |
| 空闲生活 | SyncHeartbeatAsync可调用RunIdleDeedAsync | **保留**。空闲活动自己的RememberIdleDeedPayload仍在，不能把普通工具便签删除扩大成空闲生活消失 |
| 梦话 / 夜间余温 | 专项RunNightResidueAsync | **保留独立分支**。6070bab主要改返回类型，没有把它变成普通动作或移除 |
| 身份、人格、真实历史 | 公共上下文装配 | **保留并演进**。CommonContextPack→IdentityProjection；仍由同一身份和真实来源组织 |
| 内心、实际活动、场景、关注、新识、今日轨迹 | 旧Mind动态段读取，状态面提交 | **有当前实现**。RuntimeContext统一读取；10月26日关注/场景另作轮后整理，其他字段仍在Agent输出与状态链 |
| 记忆自动预激活 | Mind前MemoryRecallLogic.Preview | **保留**。当前仍在Agent前调用；后来认知图、反证与榜单视图继续接入 |
| 插件提示扩展 | MindPromptAppends、MindJsonFields和动态MindTurnPromptAppends | **有意拆分**。9月27日5105204禁旧根协议扩展，动态钩子保留，能力说明迁入目录；旧API注释仍容易误导 |
| 执行、取消、真实回执 | 原插件调用与ProducedEvent | **增强**：执行账本、逐次可用性校验、同轮去重、取消与终态回执；没有证据表明执行反馈整体丢失 |
| 日复盘、长期认知、榜单 | 6070bab未改Tools/Migration，切换前白天认知/归档已禁用 | **不能归因9月26日**。10月30日四步收拢另有明确修订；当日日榜类型、独立回望、周期晋升变化分别看[日复盘](../DAILY_REVIEW.md) |

同提交的OneBot错误通知变更另作核对：手工拼接参数改为SerializeActionRequest统一JSON序列化，修正消息段数组被当字符串/类型名发送的问题；对应FailureProtectionChecks验证HTTP/WS外层及嵌套消息段。这是平台传输修复，不属于Agent心理/记忆能力删减。

## 1. 未迁移的情境模板：确定的是入口，不是模型能力下降

历史证据：`git show 6070bab^:src/TraceSoul2/Logic/MindLogic.cs`中DecideAsync先SelectAsync，再把templates传给动态提示；6070bab的Agent改用`Array.Empty<MindTemplate>()`。后来的AgentPromptContext不再经过该旧构造器。

当前定位：

- [MindLogic.cs](../../src/TraceSoul2/Logic/MindLogic.cs)第44行仍调用模板选择，第217行仍格式化；但当前Host/Kernel不调用DecideAsync。
- [MindTemplateLogic.cs](../../src/TraceSoul2/Logic/MindTemplateLogic.cs)保留7类模板和语义匹配。
- [AgentLoopLogic.cs](../../src/TraceSoul2/Logic/AgentLoopLogic.cs)的RunAsync/BuildStable使用AgentPromptContext和AgentLoopPrompts，不调用模板选择。
- 离线同句“给我讲个故事”可以命中旧perform模板，但实际Kernel的Agent请求没有该模板指令。

这说明“按情境额外给思考组织提示”的机制消失了；不能推出模型已经不会讲故事、接住情绪或商量。当前通用Behavior、人格/表达习惯和目标约定仍能影响这些行为。也不能原样恢复7段旧模板：其中有旧tags/leave/archive协议，release甚至提示archive=true，与切换前已经停用白天归档的实现也不一致。

建议将它列为待评估的行为继承项：先提炼仍需要的情境原则，分别测试实际Agent请求和真实行为，再决定固定提示还是轻量语义选择；不恢复旧Mind模型岗位。

## 2. 工具结果的连续性：查到了，不等于进入可回忆的经历

历史证据：`git diff 6070bab^ 6070bab -- src/TraceSoul2/Logic/KernelLogic.cs`明确删除RememberMindToolPayload及调用。旧代码把工具Payload第一行压为今日便签；这不是完整、带可靠来源的长期记忆。

当前有四种落点，不能混称“已经记住”：

1. 当前轮Workspace.Results：下一次Agent续推能看到。
2. TurnReviewRecord.PayloadJson：BuildTurnSnapshot保存截取到6000字符的结果，供审查回看；普通记忆召回与四步复盘没有把此字段作为工具原件入口。
3. 插件ProducedEvent：Kernel保存真实事件，才有机会进入后续经历链。Tavily成功搜索/读网页明确生成含来源的PluginObserved事件，因此不是所有检索都丢失。
4. Agent主动写new_fact：通过memory.today.new保存当天便签并参与后续动态上下文；依赖模型输出，不能当作每个工具结果都由程序保证保留。

TodayNewFacet将便签绑定本轮触发Moment；便签存在本身也不等于已经保存工具原件及其外部出处。后续补链应保留这一区别。

具体插件差异：

- [KernelLogic.cs](../../src/TraceSoul2/Logic/KernelLogic.cs)第613行只在有ProducedEvent时持久化事件，第1572行起是审查快照，new_fact另经MergePrivateFacets处理。
- [TavilyPlugin.cs](../../ExternalPlugins/Tavily/TavilyPlugin.cs)第199行生成搜索/网页观察来源。
- [QqQzonePlugin.cs](../../ExternalPlugins/QqQzone/QqQzonePlugin.cs)第143行ReadFromCallAsync只为空闲调用补事件；普通ReadAsync第198行返回Payload，没有ProducedEvent。发布说说另有事件，不能与读取混为一谈。
- [DailyReviewPipeline.cs](../../Tools/Migration/DailyReviewPipeline.cs)读取待处理Moment及其快照来源；没有从轮次审查Payload重建工具原件。

### 离线实际链路复现

使用当前ChatCheck程序集中的AgentProbePlugin、AgentSequenceLlm，注册Dialogue/InnerLife/MemoryNerve，内存SQLite。模拟读工具返回“答案42”，不生成ProducedEvent；Agent最终只说“看完了”，省略new_fact。随后发第二条输入，检查实际Agent请求。没有外网、真实模型或真人发送。

| 检查 | 结果 |
|---|---|
| 同轮工具续推是否含工具内容 | 是 |
| 审查快照是否含工具内容 | 是 |
| Moment经历是否含工具内容 | 否 |
| 今日新识是否含工具内容 | 否 |
| 下一轮实际请求是否含工具内容 | 否 |
| 对照：Agent明确填写new_fact后能否保存 | 是 |

这是有条件的连续性缺口，不是数据库完全没有保存，也不证明所有工具都应无差别形成认知。优先建议明确统一的“可保留观察”契约：成功读取的材料保留独立原件及来源/受众，复盘再决定是否形成事件与认知。不要把所有Payload都改成用户说过的话，也不宜恢复旧的无条件截取第一行便签。

## 3. 已修复的历史退化及其时间线

- **9月26日6070bab**：改为同一Agent续推；旧选词、模板选择、普通工具便签桥接没有进入对应新入口。
- **9月27日8fa80fb**：媒体表达改为Agent主动选择，直接文字分支清理附加表达；随后5105204明确隔离旧输出字段及插件根协议。这是另一轮改动，不应全记到6070bab。
- **9月28日c13c0a8**：恢复文字后的自动表情匹配。当前[ExpressorLogic.cs](../../src/TraceSoul2/Logic/ExpressorLogic.cs)第26～39行有Agent直接回复分支与表情映射。
- **9月30日b6c0437**：修复行动前状态/90分钟计划被最终省略丢弃、失败后停跳及发送失败阻断状态提交。当前AgentTurnStateLogic确实合并字段，Kernel先提交状态/调度再外发；[Agent框架文档](../AGENT_HARNESS.md)有当时复现与回归记录。
- **10月2日67bef2f**：当前模型少选相机的样本促成程序建立配图机会。当前Kernel的AutomaticImageProviders与相机注册相连；这是针对自主使用频率的机制调整，不能以“相机可调用”替代其存在性验证。
- **10月26日22d171b**：主动唤醒与用户入站分开，恢复同次联系意图的明确约束；关注/场景整理移入独立轮后队列。均有[职责修订](../AGENT_SENSES_PLATFORMS.md)与当前实现，不能把例行字段减少认作状态被删除。
- **10月30日**：四步复盘重写；其与9月26日Agent切换属于不同变更。主题写入断链在本地恢复时一并修复，其余已确认的流程收拢没有回滚。

## 4. 兼容代码、文档和测试造成的误判风险

这次最重要的工程发现是：**旧类仍在、旧类测试仍通过，不代表新生产入口还会使用它。**

- Program仍运行RunMindTemplateCheck和RunToolLookupCheck，直接测试模板/工具筛选类；这些测试不验证Agent请求是否注入其输出。
- MindLogic、LeaveNerveLogic、ToolLookupLogic的旧选择功能留存；当前生产Agent使用独立契约/目录。查看文件名或这些单元检查，容易误认链路完好。
- TracePluginContext仍公开MindPromptAppends/MindJsonFields并描述为器官扩展；当前Agent明确不读取它们，插件应使用目录或适合的动态提示接口。QqImageGen仍保留旧注册用于兼容，不能据注册成功推断Agent收到旧image字段。
- MemoryNervePlugin的介绍仍声称白天小复盘/旧活体写入；切换前Normalize已经清空cognition/archive，Kernel也不调用RunPostSpeakAsync。这是已有陈旧说明，不是9月26日才删掉的功能。
- 既有架构文档含历史修订，第35节“收到消息可不回”已被同日发布前更正覆盖。审计必须读取后续修订，不能任选一节作为当前需求。

后续验收应从**实际Kernel请求 → 能力执行 → 数据落点 → 重开或下一轮可读**检查继承。模板类、标签类、调用器各自正常只证明局部实现。无需为每个旧字段建立永久兼容；需要的是逐项说明保留、替代、取消和验收入口。

## 建议处理顺序

1. 优先处理工具观察的证据保留与再读取：已用实际离线主链证明缺口，直接影响新经历能否沉淀。先覆盖普通空间读取和只返回Payload的读取插件，保留Tavily已有来源语义。
2. 评估情境提示的必要部分：机制缺失确定，真实模型效果尚未对照；不恢复包含旧协议的整套模板。
3. 将兼容接口/文档标注与主链验收补齐，避免以后再次把“类还存在”当成功能仍在。
4. 工具目录目前全量注入，暂无本轮实测成本故障；插件增多时再设计候选发现及按需扩展，不直接恢复旧最多2项的硬限制。

本报告没有将未证实的语义下降当作程序错误，也没有据此自动回滚Agent、恢复白天归档、全量身份重整或重跑历史。前一轮主题恢复的构建与回归结果沿用；本轮新增的是以上内存库主链探针，临时工程使用现有模拟类型，不消耗真实API。
