# 认知网实施清单

2026-09-26 更新：本文件保留早期设计与诊断历史。固定三层岗位前提已撤销；当前四领域拼图、证据和 runtime 接入状态以 [COGNITION_PUZZLE](COGNITION_PUZZLE.md) 为准，以下历史勾选不作为当前验收清单。

目标：在保留“心智 → 外显 → 潜意识”三层结构的前提下，让潜意识基于真实 Moment 持续演化认知；认知以结构化数据保存，以自然语言注入模型。

## 执行进度

- [x] 1. 盘点现有认知、记忆、内心和 Prompt 入口，确定内核统一、平台产出 Moment 的边界。
- [x] 2. 定义统一 Experience / Moment 的来源、主体、对象、场景和证据语义。
- [x] 3. 定义认知节点字段、认知类型、状态、版本和衰退规则。
- [x] 4. 定义认知边与证据边，明确具体认知、抽象认知和修正版的关系。
- [x] 5. 定义潜意识允许执行的认知演化操作及校验规则。
- [x] 6. 将现有认知存储接口整理为上述语义，并保持旧数据可读。
- [x] 7. 建立记忆、认知、内心、痕迹和器官的统一来源接入层。
- [x] 8. 完成认知候选的语义评分、局部子图扩展、冲突保留和预算控制。
- [ ] 9. 完成结构化认知到自然语言上下文的 Renderer。
- [ ] 10. 接入心智、外显和潜意识三层的消费边界。
- [ ] 11. 增加认知网、召回和自然语言注入的回归检查。
- [ ] 12. 为机器人平台定义标准感知事件、行动请求和行动结果接口。
- [ ] 13. 更新知识库与专题文档，记录已验证和待真机验证事项。

## 接入审查（2026-09-16）

结论：目前可进入隔离联调，尚不具备直接启用新版认知网的条件。上面的勾选混合了语义设计与实现进度，第 3—8 项不能作为功能已完成的验收依据。本次只做审查和验证，未修改运行逻辑或启动真实 Host。

当前源码核对：

- 已增加认知 `About / Scope / Exceptions / Strength` 字段、兼容列和统一召回适配器；但 `ContextRecallAdapter` 与 `CognitionContextRecallSource` 没有生产调用点，也没有现有 ChatCheck 覆盖。实际对话仍由 `MemoryRecallLogic` 等旧入口召回和渲染认知摘要。
- 潜意识 `CorePrompts.Migration.CognitionJsonSchema` 仍只生成旧字段和四种操作；存储仍将 subtype 归为 `standard / trace`，摘要截为 19 字。尚无 `retire / link` 操作、抽象关系写入、完整状态和确认/挑战时间；修订旧节点使用 `revised`，与本方案的 `superseded` 不一致。
- `DayBuilder.RunCognitionFormationAsync` 清空 Fact 证据并把全部变更挂到当天最后一个 Moment，无法准确追溯各条理解的实际依据。`CommitCognitions` 仅要求触发 ID 非空，没有验证 Moment 存在；`AddCognitionEvidence` 全部写为 `supports`，包括 weaken；传入的 Fact ID 全部无效时也可能没有证据边。
- 新认知来源只使用 Tag 命中和 Cue 子串匹配；`Scene / Target / RelatedIds` 未参与召回，Freshness 固定为 0.5，没有沿认知边扩展。通用适配器将冲突候选得分乘以 0.30，不能保证保留相关反证；第一条候选可以越过字符预算，零条预算也会被改成至少一条。
- 旧 `MemoryRecallLogic.Preview / Assemble` 在事件索引或明细为空时提前返回，认知召回依附事件召回。正式接入时需允许“有认知、无匹配事件”的独立消费路径。

验证：ChatCheck 与 Host 构建均为 0 警告、0 错误；完整 ChatCheck、`--prompt-layout` 和 `git diff --check` 通过。在临时目录直接引用当前 ChatCheck 工程运行适配器复现：单条预算下相关度 1.0 的冲突候选输给相关度 0.5 的普通候选；200 字预算可渲染出 1002 字；MaxItems=0 仍返回 1 条。临时工程首次构建有 SQLite RID 兼容提示。未调用真实 LLM、未读取正式聊天数据库、未发送平台消息。

建议接入顺序：先统一潜意识输出与存储契约、逐条真实证据和修订语义；再完成认知独立召回、冲突保留、严格预算与自然语言渲染；最后接到心智动态上下文，外显消费心智结果，潜意识保持长期认知唯一写入者。验收至少覆盖旧库兼容、错误证据拒绝、反证削弱/修订、无事件时召回、预算，以及关闭历史后的长期召回。机器人接口可以另阶段完成，不应阻塞文字对话的最小闭环。

## 助手身份诊断（2026-09-15，历史结论）

早期隔离快照诊断表明，仅凭几轮协作对话不能证明长期助手身份已经形成；关闭近期历史后需单独验证长期召回。追加虚构演练并执行日构建后出现认知与证据增长，但尚不足以证明认知关联和稳定迁移。

此处只保留历史技术结论。测试配置、快照、模型原始回复与本机日志路径不进入发布文档；后续实现与离线验收以完整存在实施记录为准。

## 当前边界

- 原始 Moment 永不被认知覆盖；事实、认知和内心都是可修正的派生层。
- 潜意识是长期认知的唯一写入者；心智只读取和暂时解释，外显只表达心智结果。
- 平台负责把身体事件转换成标准 Moment，不在平台内实现独立认知逻辑。
- 当前没有机器人身体时，不人为编造关于自我或世界的经历。

## Experience / Moment 语义

Moment 是不可覆盖的经历原件。`Source` 描述经验从哪来，`Actor` 描述谁在经历，`Target` 描述指向谁或什么；三者不能用“当前用户”隐式代替。建议来源包括 `human_dialogue`、`self_reflection`、`sensory_observation`、`body_state`、`action_attempt`、`action_result`、`environment_change`、`memory_association`、`scheduled_life` 和 `platform_event`。

`Target` 使用 `self`、`other`、`relationship`、`world`、具体对象或话题标识。`Scene` 保存发生的情境，`EvidenceType` 描述是直接对话、感知、行动结果还是内在联想。平台只负责填充这些字段，潜意识再决定它是否形成长期事实或认知。

## Cognition 语义

认知节点至少包含 `owner`、`about`、`summary`、`subtype`、`scope`、`exceptions`、`strength`、`confidence`、`status`、`revision`、`created_at`、`updated_at`、`last_confirmed_at` 和 `last_challenged_at`。`subtype` 首批使用 `preference`、`boundary`、`tendency`、`relationship`、`self_model`、`expectation`。状态使用 `active`、`tentative`、`weakened`、`superseded`、`retired`。

认知不是事实、日记或硬命令。它是 Brain 对事实的可复用理解；行为由心智结合场景推导。具体认知和抽象认知都保留，抽象认知通过 `abstracts` / `exemplifies` 连接具体认知，不能覆盖具体证据。

当前已完成认知来源适配：`CognitionContextRecallSource` 将 Tag/Cue 命中的认知转换为带相关度、强度、置信度和自然语言表达的通用候选；其他领域可通过 `DelegateContextRecallSource` 逐步接入。

统一适配器通过 `DelegateContextRecallSource` 兼容现有领域召回函数，来源可以逐步迁移而不改变旧接口。筛选先按相关度、强度、置信度和新鲜度加权，再去重、保留高相关冲突候选，最后应用数量和字符预算；来源自身负责语义入口和局部关联扩展，适配器不假定认知是树。

证据边使用 `supports` 或 `challenges`，认知边使用 `revises`、`abstracts`、`exemplifies`、`related_to`。`revises` 必须创建新版本并把旧节点标记为 `superseded`，不得原地覆盖历史。

潜意识允许的操作是 `create`、`reinforce`、`weaken`、`revise`、`retire` 和 `link`。每个操作必须带真实 Moment 或 Fact 证据；没有可复用理解时只保留经历，不创建认知。
