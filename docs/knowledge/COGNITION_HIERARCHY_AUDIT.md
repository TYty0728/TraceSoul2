# 认知逐层归类与注入核对

2026-11-03，先核对源码与历史调用，再按用户要求恢复主题生长及主动选择。未读取正式运行库、调用真实模型或核对服务器运行版本。下方初审与同日复核保留当时发现；恢复后的事实以本节为准。

## 恢复实现

用户确认选词与语义归属必须恢复。本地保留Agent架构及四步日复盘，接通以下链路：

- `AgentPromptContextLogic.Context`在私密动态段列出最多12个语义主题候选，含名称与定义；`memory.topics(query)`允许继续查找。无需新增固定的模型调用阶段。
- `memory.recall(query,tags)`接受0～3个主题名称的JSON字符串数组。`KernelLogic`将经过存在性校验的完整名称交给`MemoryRecallLogic.Assemble`；事件和认知均限定到所选主题，再按既有相关性与预算取材。名称含标点不拆分；所选主题无内容时返回空，不退回全库。省略主题保留原查询能力，原有轻量预召回也保留。公开环境不展示私密主题、不接受私密选词。
- 日复盘第1步用本地向量从本段原句找最多12个主题候选，模型选择适合的旧主题，或提出有定义、正反例与域/维度归属的新主题。每事件1～4个主题，新增最多2个。程序校验短编号及字段，映射真实ID；事件、主题及检查点同事务提交。同名主题复用，不用词面相同强制语义合并。
- 第3步认知只可选择其引用原句对应事件的主题；新建与修订写关联，已有认识被印证或同内容create去重复用时可以补关联，不重复造认知。模型仍负责判断同义、修订或新含义。标签关系是检索入口，不等于提高认知置信度。
- 宿主在复盘子进程正常返回后、进入夜间余温前持主体锁刷新主题索引，后续对话能够看到新增主题。

恢复的是原设计的**域/维度 → 可生长多父主题 → 事件与认知 → 原始依据**。不声明已实现任意深度的认知父子树；也未恢复全量身份归并、认知日榜或周期晋升作为每日必经步骤。旧档案继续可读，本次不自动补分历史无主题事件；已提交检查点继续复用。

离线验证：实际Migration跨四天使用同一“牛肉面”词的意愿、口味、数学联想，分别落成三主题；第四天同义复用主题与认知，四条事件仍各自保留。新增主题随事件检查点失败一起回滚。真实Agent动作检查单/多主题召回、无结果不扩域、非法选词、标点名称、印证补关联及公开隔离。语义选择由模拟LLM提供，不能用此证明真实模型会正确分类。

Host/Migration构建、实际Migration日复盘、Agent循环/提示/契约、认知图及`--prompt-layout`（连带完整ChatCheck）均通过，`git diff --check`通过；Host/Migration零警告错误。ChatCheck使用已有模拟模型及临时库，编译有既有相机类型重复警告，本轮验证构建关闭其警告输出。未发布、部署、修改正式数据或自动重跑历史复盘。

## 恢复前审计结论

**同日复核更正：用户所记得的“选词→进入对应记忆→取细节”确有设计及旧生产调用，不是从未实现。** 初次核对将用户的“下级、分叉”过窄解释成任意深度父子认知树，遗漏了多父Tag导航与旧心智主动勾选的链路。下文“尚未实现”只适用于任意深度认知树，不可扩大成否认已有分层记忆设计。

已实现语义检索旧认知、模型判断新增/加强/修订/削弱/退役、证据留存，以及有界关联召回。尚未实现从已有认知逐层下降、寻找最合适父节点、自动挂接/分叉并按该语义树逐层注入的完整流程。

必须区分三种层次：域/维度/概念导航，认知节点间的语义关系，以及日/周/月/年/永久的重要性榜单。时间榜单不是语义父子层级。

## 当前私密日复盘主链

- `Tools/Migration/Program.cs` 的build命令进入`DayBuilder.RunAsync`，实现位于`DailyReviewPipeline.cs:64`。
- 四步为提取事件、选择事件、从入选事件原文形成认知、更新当前状态；公开资料另走`PublicExperienceLogic.BuildAsync`。
- `FormSelectedCognitions`（293行）先调用`CognitionReviewRecallLogic.Select`，按本批原文检索同环境active/weakened旧认知。候选读取上限5000，向量匹配考虑完整正文/对象/范围/例外分块，每查询片段最多3个近邻，阈值0.45，最终最多12项/8000字符。
- 检索是候选集合内的语义匹配，没有按父子边逐层遍历。之后模型输出create/reinforce/revise/weaken/retire；提示要求旧认知已表达相同认识时可返回空数组。验证与存储绑定真实来源。
- 私密四步链路未调用`CognitionConsolidationLogic.ResolveAsync`；后者确实存在“候选理解再次检索→same/revise/new/insufficient”的二次比较，但当前可见调用在公开经历链路。不能把这个类的能力自动描述成私密日复盘已执行的步骤。

## 语义树为何尚未成立

- `CognitionSliceRecord`保存领域、对象、适用范围、例外、正文、版本等，图边可表达related_to/abstracts/exemplifies/contradicts/revises。
- 底层`SqliteCognitionGraph`支持link；这种表达能力不等于已经存在自动归类器。
- 当前私密`FormSelectedCognitions`输出及校验只接受五种内容操作，不接受link，也没有父节点选择、子节点遍历、深度/路径或分叉的输出契约。
- 新建独立认知可以表达新的含义，但不会由此自动挂到某个抽象认知下面。

## 导航与注入

- `HierarchicalVectorRouterLogic.Route`实现固定三层域→维度→概念。概念候选按领域筛选，命中维度提供加分，并非严格只在某个父节点的孩子中下降，也不支持任意深度认知树。
- `LifeTagVectorLogic`把人生Tag放在概念层；不代表认知可以自动继续长出第四、第五层。
- 当前`MemoryRecallLogic.RecallPuzzle`调用`RankByMoment`→`RankConcepts`，直接排序概念Tag；不是先调用Route再沿认知树逐层检索。
- `CognitionContextRecallSource.Retrieve`由Tag/Cue、当下关注引用和文本双字片段形成种子，再做一次关联扩展；109行明确“一跳有界扩展”。之后按相关度、反证和字符预算选入，未按上位概括→下位细节逐层展开。
- `RuntimeContextLogic`读取已有榜单作为“仍重要的事”；这是重要性选择后的注入，与语义树展开是两种机制。

## 认知日榜的额外缺口

当前`DailyReviewPipeline.SelectDailyEvents`（266行）以`ListKind="event"`、`RefKind="event_index"`替换当日日榜。后续认知阶段记录ChangedCognitions，但未见将其作为cognition条目写入当日日榜的调用。

`DayLadderLogic`仍支持事件/认知分别跨期晋升，`LadderContextLogic`仍可读取已有认知榜；这只能证明已有榜单可用，不能证明新认知已自动入榜。`promote-all`仍有独立命令入口，当前四步build入口未直接调用跨期晋升。以上是本地源码链路结论，未查询正式库判断历史榜单现状。

## 对用户目标的精确差距

1. 已有：按含义寻找旧理解并判断内容如何更新。
2. 缺少：在候选认知下递归比较子节点，选择稳定的语义归属路径。
3. 缺少：明确建立/维护父子分支；处理跨分支多归属、移动与环路的规则。
4. 缺少：按查询相关性和预算从概括逐层展开细节的注入策略。
5. 新认知不入认知日榜是当前四步流程的事实，是否恢复属于另行设计决定，不能据此认定为意外回归；`docs/DAILY_REVIEW.md`及总纲第46节明确记录了2026-10-30的流程收拢。

本次是源码审计，不以模拟模型测试证明语义判断质量。旧知识文档中有关完整日复盘、认知榜单或二次归并的叙述，应结合当前实际入口使用；本记录不授权直接修改流程。

## 同日复核：选词入口与历史演变

### 用户记忆对应的真实设计

`docs/ARCHITECTURE_ALIGNMENT.md`第10～14节明确“四域→十六交叉维度→会生长的多父LifeTag→事实/认知切片”，且说明“它不是树”。第13节规定相近候选交观察算法多选，全部不合适可新增可复用Tag；第22节场景E直接使用“牛肉面”说明可同时连接饮食、午餐、地点、口味等，不强制唯一归属。

`CorePrompts.Mind.Foundation`第50行仍保留“从候选原样勾0～3个”的要求；`MindLogic.BuildTurnPrompt`第171行起输出候选标签，`MemoryRecallLogic.ListTagCandidates`按语义排候选。`MemoryRecallLogic.Assemble`第147～180行将所选标签映射为ID，按标签筛事件索引，再取事件条目做向量检索，最后格式化具体记忆。这些选词确实用于缩小后续取材范围。

写入侧另有`MemoryObservationLogic`与`CorePrompts.MemoryObservation`：候选Tag多选、全部不合适时新增Tag、事实挂接所选Tag。它体现语义主题生长与多路归属，不等于每条消息永远固定分类，也不等于必须创建一个无限深的目录树。当前普通对话不应仅凭这些类存在就推断仍调用观察写入。

### 旧主链确实调用过

读取Git提交`6070bab`（2026-09-26，0.1.15统一Agent循环）的父版本：

- 旧Mind动态提示无条件列出ListTagCandidates生成的候选。
- Kernel在心智决策后检查decision.WantsMemory()，将完整decision（包含tags）传入MemoryRecallLogic.Assemble。
- `6070bab`差异删除上述直接调用，改为Agent行动循环；memory.recall构造新的MindDecisionData时仅设置beat/query。

后续`a19904d`（2026-09-28）的Agent提示改为AgentPromptContextLogic；当前主链未使用旧Mind标签候选提示。

### 当前选词为何没有走通

- `AgentOutputData`不包含tags字段；当前契约不要求模型勾选旧标签。
- `AgentLoopLogic.BuildCatalog`的memory.recall仅声明query参数。
- `KernelLogic.ExecuteAgentActionAsync`第566行将beat/query传入Assemble，没有传tags。因此Assemble中的所选标签列表为空，事件索引阶段不会因模型选词收窄。
- 当前日常路径先Preview自动预召回，再由Agent按需调用memory.recall(query)。认知侧仍自动排序Tag/Cue并做有界图关联，不能说整个标签能力已经被删除。
- 所以准确结论是：多层/多父语义组织与主动选词机制有过真实实现；当前主链已经改变，旧主动选词入口没有继续接到Agent召回。是否恢复或改进需另行决定，不能未经授权回滚整个Agent框架。
