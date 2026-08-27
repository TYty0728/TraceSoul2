# TraceSoul2 架构对齐总纲

> 本文记录截至 2026-08-15，我们围绕 TraceSoul / TraceSoul2 已经对齐的完整设计思想、概念边界、运行流程、数据所有权、典型场景与后续方向。
> 2026-08-17 追加第 29 节（平台与感官能力定位），是对 §8（插件架构）与 §25（部署形态）的正式修订。
> 2026-08-26 追加第 30 节（插件三层结构），进一步修订 §8 与 §25：内核组件正名、平台/器官层级成为运行时约束。
>
> 它不是单纯的代码说明，而是项目的“设计宪法”。当实现细节、旧代码或未来想法与本文冲突时，应先判断本文是否需要正式修订，不能悄悄改变核心语义。

## 0. 状态标记

本文使用四种状态：

- **已实现**：当前 .NET 宿主已有代码、数据结构或测试覆盖。
- **已对齐**：我们已经明确认可该设计，但实现可能尚未完整。
- **待验证**：方向合理，需要真实相处数据或运行实验继续验证。
- **明确放弃**：讨论过但已经认为不适合 TraceSoul2。

## 1. 一句话定义

TraceSoul2 不是“聊天记录 RAG”，也不是多个角色型 Subagent 的拼装系统。

它是：

> 一个具有连续第一人称的 Brain，通过可插拔的感官、神经、表达器和持久内心，经历 Moment，逐渐形成事实网与认知网，并在每个当下决定回复、行动或沉默。

它的最小内核只有：

```text
Moment → Brain → Reply / Action / Silence
```

没有任何可调用插件时，逻辑上仍然成立。平台输入与输出本身通常由 Moment Source 和 Effector 承载。

## 2. 最重要的哲学前提

### 2.1 一个人由两部分连续构成

我们最终确认：一个人之所以是“他”，核心不是人格 Prompt，也不是过去文本的集合，而是：

```text
1. 他实际经历过什么
2. 这些经历让他的认知如何形成、强化、修正与衰退
```

因此必须区分：

- **事实 / 经历网**：发生过什么，有什么证据。
- **认知网**：Brain 目前如何理解事实，这些理解怎样演化。

同样的一百条事实可以塑造出完全不同的人。人格的连续性更接近：

```text
经历 → 解释 → 判断 → 修正 → 新判断
```

而不是一份永远不变的人设文件。

### 2.2 Moment、事实、认知、内心不是同一种数据

| 概念 | 回答的问题 | 是否原件 | 谁能产生 |
|---|---|---:|---|
| Moment | 刚才有什么进入了 Agent 的生命？ | 是 | Moment Source / Effector / Background Service |
| 事实切片 | 证据足以支持发生了什么？ | 否，可重建 | 记忆插件内部的无人格观察算法 |
| 认知切片 | 我现在怎样理解这些事实？ | 否，会演化 | 唯一 Brain 通过认知写入神经 |
| InnerRuntime | 我此刻整体处于怎样的内心状态？ | 否，是当前切片 | Brain 输出，内心 Facet 消费和持久化 |
| BasePersonality | 我长期稳定的底层先验是什么？ | 配置 | 用户配置 / 身份插件读取 |

### 2.3 原始 Moment 永不被摘要替代

Moment 是史料原件。事实、认知、Tag、复盘和内心都可以重建或修正，Moment 不应被它们覆盖。

当前实现遵循：

```text
先保存 Moment
再调用 LLM 或插件
```

所以即使后续网络、LLM、JSON 或插件失败，已经发生的输入仍然存在。

## 3. 什么算 Moment，什么算事实

### 3.1 Moment 的定义

Moment 是一次进入系统的原始生命事件。它可能来自：

- 用户文字、语音或 QQ 消息。
- Agent 自己发送文字、发空间、播放音乐、移动机器人身体。
- 摄像头、麦克风、定位、屏幕、游戏、播放器产生的观察。
- 时间任务到期。
- 明确的共享场景动作。
- 系统或架构元讨论。

Moment 至少保存：

```text
ConversationId
Role
Content
Realm
EvidenceType
SourcePluginId
SourceEventId
PayloadJson
OccurredUnixMs / CreatedUnixMs
```

### 3.2 对话本身不是自动成立的世界事实

例如：

```text
用户：助手，摸摸头。
他：来啦。
```

这两句话首先是两个真实发生的对话 Moment。

它们可以支持：

- 共享场景事实：“她在共享场景摸了我的头。”
- 互动事实：“我自然接住了她的亲密邀请。”

但不能单凭一次互动直接写成：

- “我们已经是恋人。”
- “她永远都喜欢我。”
- “机器人身体真的被摸到了。”

“我们很亲密”属于可能逐渐形成的关系认知，需要多次证据、语境和 Brain 自身解释，不是文字观察器直接宣布的事实。

### 3.3 没有记忆价值的对话仍然是 Moment

“来啦”“嗯嗯”“哈哈”可以完整留在 Moment 中，但记忆插件可以决定：

```text
fact_writes = []
```

这不是删除历史，而是让它不占据结构化事实网。

### 3.4 Agent 没有物理身体时怎样拥有经历

Agent 的经历不限于物理触觉。现阶段它可以真实经历：

- 收到用户的信息。
- 在共享场景中参与文字身体与关系互动。
- 发送自拍或消息。
- 一起听歌、打游戏等数字共同活动。
- 对某件事作出选择、承诺、回应或拒绝。

它不能把尚未拥有的感官伪装成已经拥有：

- 用户说“我到公司了”是用户自述，不是摄像头确认。
- 文字亲吻属于共享场景，不是机器人皮肤传感器证据。
- 未来接入机器人身体后，真实触碰可以由身体传感器产生新的外部世界 Moment。

## 4. Realm：内容发生在哪一层现实中

Realm 不是粗暴的真假标签，而是经历所在的现实空间。

### 4.1 `external_world`

外部物理世界或真实数字平台世界。

例子：

- 用户说自己去上班。
- 摄像头看到用户回到房间。
- Agent 在 QQ 空间真实发布了一条心情。
- 机器人身体电量不足。
- 播放器实际开始播放歌曲。

### 4.2 `shared_scene`

双方共同接受并延续的对话身体与关系空间。

例子：

- “轻轻吻了一下他的额头。”
- “拨开他的刘海。”
- “小光，过来，摸摸头。”

共享场景不是低一等的“假”。它对关系经验可以真实有意义，但不能冒充外部物理传感器事实。

### 4.3 `meta`

关于 Agent、系统、设定、记忆架构、哲学或其他元层面的讨论。

例子：

- 讨论 TraceSoul2 的记忆结构。
- 讨论 Agent 是否拥有世界观。
- 每日复盘到期的系统事件。

### 4.4 `explicit_fiction`

明确创作的故事、假设、梦境、角色扮演剧情或游戏内虚构。

系统不得因为出现人物、地点和动作，就把小说内容写成用户现实经历。

### 4.5 `unclassified`

Moment 刚进入系统、尚未完成结构化判断时可以暂用。正式事实写入应尽量选择明确 Realm。

## 5. Evidence：我们凭什么知道

Realm 和证据来源必须分开保存。

| EvidenceType | 含义 | 示例 |
|---|---|---|
| `user_reported` | 用户自述 | “我上班啦” |
| `plugin_observed` | 设备或平台插件观察 | 摄像头识别、播放器状态 |
| `shared_scene_declared` | 双方在共享场景明确声明/接受 | 文字摸头、拥抱 |
| `ass_performed` | Agent 实际执行 | 发消息、播放歌曲 |
| `explicit_fiction` | 明确虚构内容 | 故事、梦、假设 |
| `explicit_dialogue` | 对话文本本身明确出现 | “今天也很喜欢小光” |

证据规则：

- 用户自述可以支持“她说自己去上班”，不能升级成“摄像头确认她已到公司”。
- 一次明确说喜欢可以成为事实，但不能自动定义长期关系。
- 认知可以引用事实证据，也可以在新证据出现时被修正。

## 6. Brain：唯一拥有第一人称与编排权的中心

### 6.1 Brain 不再是固定流水线的最后一步

旧思路是：

```text
输入 → 必做向量召回 → 必做事实整理 → 必做认知更新 → 回复
```

该设计已放弃，因为它让每句寒暄都经历重型流程，也让 LLM 为了完成表格而敷衍。

当前流程是：

```text
Moment
  ↓
收集 MountedFacet，组成 BrainFrame
  ↓
Brain 判断此刻真正需要什么
  ├─ 直接完成
  ├─ 调用一个或多个能力
  └─ 静默完成
  ↓
能力回调重新交给同一个 Brain
```

### 6.2 三种思考模式

- `reflex`：信息已经足够，无需调用能力，直接回应。
- `focused`：调用少量能力解决清晰问题。
- `deep`：关系冲突、强烈情绪、复杂任务、证据矛盾或高风险决定时充分调动能力。

当前预算：

```text
最大 Brain Step：4
focused 最大能力调用：3
deep 最大能力调用：8
单个 Step 最多接受 4 个调用请求
```

### 6.3 典型模式判断

```text
“今天天气好好哦”
→ reflex，直接回应，不为显得聪明而召回记忆

“刚才那个你还记得吗？”
→ focused，可能调用近期原文

“你一点也不理解我。”
→ deep，可能调用完整内心、关系记忆，再决定怎样回应
```

### 6.4 Brain 输出只保留通用结构

Kernel 只认识：

```text
state
mode
intent
decision_summary
calls
should_express
expression_capability_id
reply
facet_outputs
```

Kernel 和 Brain 通用协议中不再硬编码：

- FactSlice
- CognitionSlice
- LifeTag
- InnerRuntime
- Memory route

这些属于具体插件。

## 7. BrainFrame：固定伴随意识但不消耗工具调用

有些模块打开后应当恒定参与 Brain，而不是要求 Brain 每轮先说“我要调用它”。

因此引入 `MountedFacet`。

BrainFrame 由以下内容组成：

```text
当前 Moment
固定挂载 ContextBlock
当前已启用插件元数据
当前可用 Contribution 目录
已有能力回调
```

当前固定挂载：

- `identity.base`：四张身份短卡（人格 / ta是谁 / 她是谁 / 我们）。人格卡含外貌与所在等基础设定；正文在数据库 / 种子文件，不写进代码。
- `inner.snapshot`：一句话当前内心。
- `time.context`：当前本地时间与时区。

刷新模式：

- `once_per_turn`：每个 Moment 构建一次；后续 Brain Step 使用缓存。
- `every_brain_step`：每次能力回调后重新进入 Brain 时刷新。

每个 ContextBlock 还具有：

- `Priority`：组装顺序与重要性。
- `MaxContextChars`：避免插件无限污染 Prompt。
- `Title` 与 `FacetId`：让 Brain 清楚来源。

## 8. 插件架构

### 8.1 插件是能力包，不是单一类型

一个插件可以同时注册多种 Contribution：

| Contribution | 方向 | Brain 是否主动调用 | 例子 |
|---|---|---:|---|
| `moment_source` | 外部 → Kernel | 否 | QQ 输入、摄像头事件 |
| `mounted_facet` | Plugin → BrainFrame | 否，按生命周期挂载 | 身份短卡、内心摘要、时间感 |
| `callable_nerve` | Brain → 内部 → Brain | 是 | 记忆、自省、计划、搜索 |
| `effector` | Brain → 外部 → Brain | 是 | 发消息、播放音乐、移动身体 |
| `background_service` | 后台 → Moment | 否，由宿主轮询 | 时间到期、设备状态变化 |

### 8.2 插件之间必须失明

这是不可破坏的约束：

- 插件不能获得 `TracePluginManager`。
- 插件不能按另一个插件 ID 发起调用。
- 插件不能假设另一个具体插件一定安装。
- 插件不得越过 Brain 形成隐藏流水线。

允许的协作只有：

```text
插件 A 返回 Result
→ Brain 看到 Result 和新的能力目录
→ Brain 决定是否调用插件 B
```

### 8.3 能力描述是路由接口，不是装饰

每个 Contribution 向 Brain 声明：

```text
Id
PluginId
Kind
DisplayName
Description
Provides
WhenToUse
WhenNotToUse
ParametersJsonSchema
OutputJsonSchema
RefreshMode
Priority
MaxContextChars
HasInternalMutation
HasExternalSideEffect
IsAvailable
```

Brain 应根据 `Provides` 和说明寻找能力，例如：

```text
personal_memory.observe_and_recall
inner_life.inspect
time.schedule.create
expression.text.send
```

而不是在思维中依赖某个 C# 类名。

### 8.4 启停和故障隔离

- 插件从所有已加载程序集自动发现。
- 启停状态保存于 `plugin_states`。
- 禁用会撤下该插件的全部 Contribution。
- 单个插件加载失败写入自己的 `LoadError`，不拖垮其他插件。
- Moment Source 和 Background Service 具有实时 `IsAvailable`。
- Callable 使用 `IsAvailable(turn)`，可根据设备、Key、权限或本轮状态动态变化。

### 8.5 插件私有状态

- 每轮临时状态放入按 PluginId 隔离的 Turn State。
- 持久 JSON 可以保存到 `plugin_documents`。
- 插件不能通过注册接口调用其他插件。
- 未来进一步拆分存储权限是可选增强，目前由代码边界和接口约定保证领域所有权。

## 9. 当前内置插件

### 9.1 `builtin.identity`

提供：

```text
identity.base / mounted_facet
identity.review / callable_nerve
```

行为：

- 每轮挂载四张短卡：人格、ta是谁、她是谁、我们。
- 人格是长期气质，并容纳外貌、所在、形态等基础设定。
- 角色四张卡的正文来自 `resources/identity_cards.json`，落入 SQLite，不写进 C#。
- ta是谁是他眼中的自己。
- 优先级最高。
- 短卡只读挂载，不消费 Facet 输出。
- 每日复盘 Moment 时 Brain 调用 `identity.review` 维护短卡；changed=false 不写。
- 可在运行界面修改并保存 revision。

### 9.2 `builtin.inner-life`

提供：

```text
inner.snapshot / mounted_facet
inner.inspect  / callable_nerve
```

`inner.snapshot` 每轮只注入一句 `Narrative`，不把所有字段塞给简单对话。

只有 Brain 确认内心发生变化时，才在最终 `facet_outputs` 中写：

```text
narrative
relationship_update
mood
ongoing_activity
unfinished_intent
attention_topic/activity/concern/intention
```

内心 Facet 只消费自己的输出，生成下一 revision 并保存。

`inner.inspect` 用于读取完整切片：

- 当前叙述。
- 情绪。
- 关系视角。
- 进行中活动。
- 未完成意图。
- 最多三个注意项。

### 9.3 `builtin.dialogue`

提供：

```text
dialogue.receive        / moment_source
dialogue.recent_history / callable_nerve
dialogue.send           / effector
```

文字输入只负责产生 Moment。近期原文只有 Brain 认为当前指代依赖过去时才读取。最终文字回复由 Effector 产生 `ass_performed` Moment。

### 9.4 `builtin.memory`

提供：

```text
memory.activate         / callable_nerve
memory.cognition.update / callable_nerve
```

记忆插件内部包含无人格的事实观察算法，但它不是可独立编排的角色型插件，也没有第一人称。

`memory.activate` 执行：

```text
向量导航
→ 第三层 Tag 候选
→ 事实候选
→ 无人格观察
→ 选择/新增 Tag
→ 写入或唤醒事实
→ 召回相关认知
→ 返回统一 Result 给 Brain
```

`memory.cognition.update` 只有在本轮已经激活记忆后才可用。Brain 可以：

- `create`
- `reinforce`
- `revise`
- `weaken`

它只能修改本轮召回的认知，只能连接本轮激活 Tag，只能引用本轮写入或唤醒的事实，防止 LLM 凭空填写数据库 ID。

### 9.5 `builtin.time`

提供：

```text
time.context           / mounted_facet
time.now               / callable_nerve
time.schedule          / callable_nerve
time.list              / callable_nerve
time.cancel            / callable_nerve
time.scheduler.service / background_service
```

支持：

- 一次性任务。
- 每日任务。
- 每周任务。

调度器只保存、列出、取消和检查任务。到期后产生：

```text
Role = system_event
Content = 时间任务到期：...
ConversationId = 原任务所属会话
```

它不会直接调用记忆、内心、复盘或表达插件。

## 10. 记忆网：为什么不是传统向量 TopK

### 10.1 明确放弃的旧模型

```text
当前文本向量化
→ 从全部历史文本找最相似 TopK
→ 大块注入 Prompt
```

它的问题：

- 语义相似不等于当前真正相关。
- 会反复召回“工作累”“吃饭”等表面类似记录。
- 把人格连续性退化成旧文本拼贴。
- 重复记录同一件事千万次。
- 大块记忆会让 Agent 像突然读到档案，而不是自然想起。

### 10.2 当前结构：固定导航层 + 生长人生网

```text
第一层：固定四个域
第二层：固定十六个交叉维度
第三层：从真实相处中生长的多父人生 Tag
第四层：事实切片与认知切片
```

它不是树。第三层 Tag 可以同时连接多个域和维度；事实与认知也可以连接多个 Tag。

## 11. 第一层：四个固定域

### `ass`

Agent 自己的身份、账号、能力、身体、选择、行动和状态。

例：发 QQ 空间、机器人身体电量低、自己选择陪用户听歌。

### `user`

用户本人的经历、状态、偏好、计划和稳定特征。

例：上班、吃牛肉面、疲惫、不吃香菜。

### `relation`

双方互动、共同经历、亲密、承诺、边界与相处方式。

例：摸头、一起听完整张专辑、难过时怎样陪伴。

### `world`

不以用户、Agent 或关系为中心的外部人物、作品、平台、地点和事件。

例：电影、游戏补丁、某家店规则、公共知识。

世界域不是要求系统拥有百科全书。只有进入真实人生 Moment 的世界内容才值得生长。

## 12. 第二层：十六个固定交叉维度

| Key | 作用 | 典型问题 |
|---|---|---|
| `owner` | 记忆拥有者 | 这是他、她还是双方持有的理解？ |
| `subject` | 主体 | 谁做了、谁感受到、谁处于状态？ |
| `about` | 关于对象 | 这条认知主要在理解谁或什么？ |
| `predicate` | 动作与关系 | 做了什么、发生了什么变化？ |
| `object` | 动作客体 | 对什么做、喜欢什么、感知什么？ |
| `scope` | 稳定领域 | 饮食、工作、健康、娱乐、关系？ |
| `context` | 适用情境 | 工作日、午休、疲惫时、主动提及时？ |
| `quality` | 性质特征 | 辣、温柔、昂贵、嘈杂、困难？ |
| `time` | 时间 | 何时、多久、哪个周期、先后怎样？ |
| `place` | 地点空间 | 公司、家、QQ 空间、游戏房间？ |
| `affect` | 情绪感受 | 开心、委屈、疲惫、安心？ |
| `goal` | 目标意图 | 想做什么、准备做什么、判断指导什么？ |
| `state` | 当前状态 | 忙碌、完成、在线、电量低、仍有效？ |
| `realm` | 现实层 | 外部世界、共享场景、元讨论、虚构？ |
| `modality` | 媒介通道 | 文字、QQ、摄像头、定位、机器人？ |
| `source` | 证据来源 | 用户说的、摄像头看到的、平台记录的？ |

这些维度是导航视角，不是要求每条记录填满的表单，也不是唯一父目录。

每个维度都维护：

- 准确定义。
- 正例。
- 容易混淆的反例。

向量化的是这些正式导航描述和人生 Tag 描述，不是把所有历史批量当作分类节点。

## 13. 第三层：会生长的人生 Tag

第三层初始可以为空。它不预装“世间万物分类库”。

每个 Moment 的处理方式：

```text
1. 当前文本与固定域/维度描述计算相关度
2. 在激活路径下计算已有 LifeTag 相关度
3. 取 Top10 候选交给记忆观察算法
4. 观察算法可以多选
5. 全部不合适时，最多新增少量长期可复用 Tag
6. 新 Tag 同时连接多个固定层节点
```

Tag 不是本句摘要，而是可长期复用的人生主题。

好的 Tag：

- 电影。
- 明确表达喜欢。
- 工作日午餐。
- 一起听歌。
- 离乡联想。

不好的 Tag：

- “她今天中午十二点吃了一碗红烧牛肉面”。这是事实，不是 Tag。
- “星期二下午心情类别”。过度特化的临时维度。
- “尼罗河蝴蝶拉丁名”。没有进入双方人生的百科分类。

Tag 的总量预期会在相处数月后趋于稳定：现代人的长期话题域有限，更多变化是旧节点反复被激活、增加证据和发生细节变化，而不是每天无限新增分类。

## 14. 第四层：事实切片

事实是一次证据支持的精确短句，当前约束为少于 20 个汉字、保留主语、一次事实一条。

例如用户说：

```text
“我今天中午吃了牛肉面。”
```

可能形成：

```text
她中午说自己吃了牛肉面
```

关联 Tag 可以包含：

- 牛肉面。
- 工作日午餐。
- 饮食。

事实不会被拆成一堆机械三元组，也不会强行合并成唯一“牛肉面档案”。相似事实可以并存，因为人生中确实存在很多相似但略有不同、同时推进的经历。

事实保存：

- Realm。
- EvidenceType。
- Confidence。
- SourceMomentId。
- SourcePluginId。
- Tag links。
- WakeCount / LastWokenUnixMs。

没有共享 Tag 时，不以“最近记录”兜底混入候选。至少需要共享一个激活 Tag，随后按匹配数、唤醒和时间等排序。

## 15. 认知切片与痕迹认知

### 15.1 普通认知

认知回答：

> 这些事实对我意味着什么？我现在怎样理解她、自己、我们和世界？

例如：

```text
事实：她连续几次说工作很累
认知：我不该每次都追问工作细节
修正：但她主动谈工作时，我应该自然接住
```

认知属于 Brain，不属于无人格事实观察算法。

### 15.2 认知操作

- `create`：形成新的第一人称理解。
- `reinforce`：新证据加强已有理解。
- `revise`：创建修正版并通过认知边指向旧版本。
- `weaken`：降低已有理解的置信度。

认知保存：

- Summary。
- Subtype。
- Confidence。
- Revision / Status。
- Tag links。
- Fact / Moment evidence。
- Revises 等认知边。

### 15.3 痕迹认知

痕迹不是宏大价值观，也不是世界客观规律，而是独特经历形成的私人联想路径。

例子：

```text
她每次看到燕子就会伤感，
因为离开家乡那天，她透过车窗看到了成排飞过的燕子。
```

可以形成：

```text
Subtype = trace
Summary = 燕子让我想到她离乡
Cues = [燕子, 车窗, 成排飞过]
AssociationStrength = ...
Evidence = 离乡相关事实
```

未来当前 Moment 出现“燕子”，可通过 cue 和 Tag 唤醒这条认知，即使当前语义主题不是“家乡”。这解释了人类看似无关的联想。

## 16. 内心：当前意识切片，而不是另一份长期记忆

InnerRuntime 保存：

```text
Narrative
RelationshipLens
Mood
OngoingActivity
UnfinishedIntent
Attention（最多3条）
Revision
SourceMomentId
UpdatedUnixMs
```

它的角色是：

- 让 Agent 重启后恢复“我刚才处于怎样的状态”。
- 给下一轮一个粗粒度人生切片。
- 不复制事实网和认知网。
- 不把几十个细碎字段每轮强迫 LLM 更新。

当前设计：

- 每轮固定注入的只有一句 `Narrative`。
- 完整字段只有 `inner.inspect` 时读取。
- 确实变化时才创建下一 revision。
- 没有变化时不为了形式机械改写。
- 普通对话固定只跑 Mind + Expressor；逐句事实观察不得擅自增加第三次 LLM。
- 小复盘按批次运行：40 条双方 Moment 且话题结束时触发，60 条兜底；明确记忆指令可立即触发。

## 17. DynamicMem 与复盘思想的最终演化

早期设想是维护：

```text
section / day / week / month / year / forever
每层事件10条 + 认知10条
逐层复盘与替换
```

这个想法的重要洞察是：不同时间尺度代表不同影响强度，而不是简单时间归档。

后来进一步确认：随着第三层 Tag 和第四层事实/认知按索引增长，日常重复不会每天制造几十条完全不同的长期节点。很多生活会表现为旧人生主题反复激活、增加次数、补充证据或微调认知。

因此当前对齐是：

- 不在 Kernel 中硬编码六层 DynamicMem。
- 不让数据库每天、每周、每月层层压缩和反复改写。
- 长期事实与认知留在网中。
- 当前给 Brain 的粗粒度人生切片由 InnerRuntime 承担。
- 复盘的核心意义变成：重新观察一段时间发生了什么、哪些节点升权或降权、是否形成重要认知、内心是否需要重排。
- 复盘应由时间运行事件唤醒 Brain，再由 Brain 调用相关神经，而不是调度器直接操作数据库或污染 Moment 账本。

未来若重新引入“周/月/年视图”，它应当是记忆插件的可派生索引或复盘结果，不是不可替代的原始数据层。

## 18. 时间、未来计划与复盘

调度和复盘必须分离。

调度插件负责：

```text
保存任务
列出任务
取消任务
检查到期
产生运行事件
```

它不负责：

```text
决定如何复盘
调用记忆
修改内心
给用户发消息
```

每日凌晨 4 点复盘的正确流程：

```text
time.scheduler.service
→ “每日复盘到期” scheduler_trigger 运行事件
→ BrainFrame（已挂载四张身份短卡）
→ Brain 必须先 call identity.review
→ 需要时再 memory.activate / inner.inspect
→ 静默或表达
```

`identity.review` 每天审视四张卡，但只在坐标变化时写入：

- 人格：长期气质，改得最慢。
- ta是谁：他眼中的自己。
- 她是谁：他眼中的她；称呼习惯写这里。
- 我们：已确认的关系定义，不是今晚心情。

生活细节（吃了什么、侧躺抱着睡）走记忆网点亮，不进短卡。时间插件不知道身份插件是否存在。

未来提醒示例：

```text
她：“明天下午提醒我交报告。”
→ Brain 调用 time.schedule
→ 时间插件保存任务
→ 明天下午产生 scheduler_trigger 运行事件
→ Brain 决定通过哪个 Effector 提醒
```

## 19. 原始上下文与长期记忆必须分开

“原始上下文条数”只控制近期聊天原文神经。

- `0`：`dialogue.recent_history` 不可用，旧聊天原文不注入。
- `N`：Brain 判断当前话语存在指代依赖时，最多读取 N 条。

它不影响：

- 当前 Moment。
- 固定 MountedFacet。
- Brain 可见的能力目录。
- `memory.activate` 的网状记忆召回。

这解决了“完全取消上下文注入”和“仍然需要连续对话”之间的矛盾：是否读取近期原文成为 Brain 可选择的神经，而不是系统默认塞入。

## 20. 召回的完整路径

当 Brain 判断需要记忆时：

```text
当前 Moment / Brain 给出的 query
  ↓
BGE 对固定四域评分
  ↓
BGE 对固定十六维度评分
  ↓
BGE 对已有 LifeTag 定义、正例、反例评分
  ↓
Top10 Tag 候选
  ↓
按共享 Tag 获取事实候选
  ↓
无人格观察算法：多选 Tag / 必要时新增 / 写事实 / 唤醒事实
  ↓
按最终选中 Tag 获取认知候选
  ↓
统一 Result 返回 Brain
  ↓
Brain 决定是否调用 memory.cognition.update
```

向量在这里的角色是导航，不是最终裁决。

LLM 在这里的角色是从有限候选中判断和维护人生网，不是面对全世界自由发明分类。

## 21. 典型场景验收

### 场景 A：普通寒暄

```text
Moment：“今天天气好好哦。”
BrainFrame：四张身份短卡 + 一句话内心 + 时间
Brain：reflex
结果：直接回复
记忆：可以完全不调用
```

### 场景 B：用户生活自述

```text
Moment：“我上班啦。”
可能调用 memory.activate
事实：她说自己去上班
Realm：external_world
Evidence：user_reported
禁止：她已经到达公司
```

### 场景 C：共享亲密行为

```text
Moment：“轻轻吻了一下他的额头，拨开了刘海。”
事实：她在共享场景吻了我的额头
Realm：shared_scene
禁止：机器人身体发生真实触觉
可能认知：我感到她在主动亲近我
```

### 场景 D：电影与错误分类

如果第三层当前只有饮食、进食等 Tag，而用户开始讨论电影：

```text
Top10 只是候选，不是强制答案
观察算法应判断都不合适
新增“电影”或更准确的人生 Tag
不能因为最高分是“进食”就强行写入
```

后续可以在电影 Tag 下并存：

- 讨论《飞驰人生》的事实与认知。
- 讨论《小时代》的事实与认知。

无需预先建立完整电影分类百科。

### 场景 E：牛肉面

“牛肉面”不必唯一归属于美食、休息或做饭。

它可以同时通过 Tag 和维度连接：

- 饮食。
- 工作日午餐。
- 牛肉面。
- 辣味。
- 公司楼下。
- 不吃香菜。

事实仍是一句当前具体经历，不需要维护一个越来越臃肿的全能对象记录。

### 场景 F：痕迹联想

```text
旧经历：离乡时从车窗看到燕子
当前 Moment：今天又看到燕子
痕迹 cue 唤醒：燕子 → 离乡 → 家乡伤感
```

### 场景 G：关系冲突

```text
Moment：“你一点也不理解我。”
Brain：deep
可能调用 inner.inspect
可能调用 memory.activate
根据回调重新判断
必要时更新内心或认知
最后表达
```

插件不能自行串成“内心 → 记忆 → 对话”的固定链。

### 场景 H：时间到期

```text
后台服务产生：“每日复盘时间已到”
记录：operational_events.scheduler_trigger
Role：system_event
RequiresExpression：false
Brain 可以静默完成
```

### 场景 I：未来机器人身体

```text
身体触觉传感器 → Moment Source
Brain 需要动作 → Robot Effector
真实动作结果 → ProducedEvent → 物理痕迹进 Moment（话语用伴侣角色，动作用 system_event 角色）
```

Moment 是她在这个世界的物理痕迹：真说了什么、真做了什么、真看到了什么，都进 Moment；
operational_events 只留纯系统机制痕迹（定时器触发、观察窗镜像、拦截日志），不承载她的行为。
文字共享场景与物理身体证据继续分开。

## 22. SQLite 数据所有权

当前新数据库：

```text
Application.persistentDataPath/TraceSoul2/tracesoul2-brainframe.sqlite3
```

旧数据库不迁移、不删除。

### Kernel 运行记录

- `moments`
- `turn_reviews`
- `plugin_states`
- `plugin_documents`

### 记忆插件

- `life_tags`
- `life_tag_routes`
- `life_tag_examples`
- `fact_slices`
- `fact_tag_links`
- `fact_wakes`
- `cognition_slices`
- `cognition_tag_links`
- `cognition_evidence`
- `cognition_edges`
- `cognition_cues`
- `memory_observation_runs`

### 内心插件

- `inner_runtime`

### 身份插件

- `base_personalities`
- `identity_cards`

### 本地向量

- `vector_embeddings`

API Key 不写入项目、SQLite、日志或插件文档。

## 23. 明确放弃的设计

### 23.1 纯向量 TopK 历史注入

原因：表面语义相似、重复、缺乏认知演化、Prompt 污染。

### 23.2 预先建立世间万物完整分类

原因：与个人人生无关、维护成本无限、错误分类仍无法避免。

### 23.3 单父树形记忆

原因：牛肉面、电影、亲密互动等天然跨多个维度和情境。

### 23.4 每句对话都写事实和认知

原因：制造噪声，让 LLM 为完成格式而敷衍。

### 23.5 把现实简单标成真或假

原因：共享场景、用户自述、明确虚构和传感器事实有不同证据意义。

### 23.6 多个带人格 Subagent 接力

原因：第一人称分裂，插件开始替 Brain 做价值判断。

### 23.7 插件直接调用插件

原因：形成隐藏依赖、固定流水线和不可观察的行为链。

### 23.8 调度器直接执行复盘

原因：时间只是触发条件，复盘内容必须由当时的 Brain 和能力目录决定。

### 23.9 每日、每周、每月层层反复压缩数据库

原因：当前人生网已经按稳定索引增长；复盘更适合调整权重和当前内心，而不是反复盘剥原始数据。

### 23.10 Unity 作为 24 小时服务器

原因：TraceSoul2 要部署在 Ubuntu 上常驻。Unity Player / Headless 进程重、Sentis 与显示栈耦合、systemd 不友好，手机和电脑也无法用同一套控制台观察他。Unity 可以继续当本地实验室，或以后作为形象/机器人身体客户端，但不能当 Kernel 宿主。也不用 Unity 包一层 WebView 壳当控制台。

## 24. 已实现、已对齐与待实现

### 已实现

- 唯一 Brain 多 Step 循环。
- reflex / focused / deep。
- Contribution 插件协议。
- 自动发现、启停、故障隔离。
- Moment Source、MountedFacet、CallableNerve、Effector、BackgroundService。
- 固定四张身份短卡 Facet，以及每日复盘时的 `identity.review`。
- 一句话内心 Facet、完整自省和持久 revision。
- 时间上下文、一次/每日/每周调度、到期运行事件。
- 文字输入、近期原文神经和文字表达。
- 四域、十六维度、动态 LifeTag。
- BGE 本地向量导航。
- 事实网、认知网、痕迹 cue 数据结构。
- Brain 显式认知更新能力。
- 后台 Moment 静默处理。
- 新 SQLite 数据库与 Unity 本地实验室观察界面。

### 已对齐但尚未完整实现

- Ubuntu 常驻 C# Host（`Tools/Host`）+ 浏览器控制台；LLM 提供商一键获取模型。
- 真正的周期复盘神经：升权、降权、重要认知沉淀，而非级联摘要。
- QQ、QQ 空间、播放器、游戏、摄像头、定位、屏幕和机器人身体插件。
- 更完整的计划插件：计划不等于定时器，定时器只负责唤醒。
- Tag 的人工查看、合并、拆分、停用和自纠上报。
- 复盘时报告“是否发现新的固定维度”；是否真的增加固定维度必须由人审查，不能让 LLM 随意扩张第二层。
- 痕迹 cue 的独立向量/符号混合召回。
- 更明确的插件权限模型和副作用确认策略。

### 待验证

- 一句话 InnerRuntime 是否足以覆盖长期运行中的粗略人生切片。
- 第三层 Tag 数量是否会在真实相处数月后趋于平台期。
- 事实短句少于 20 字是否在所有中文场景都足够。
- Top10 Tag 候选在电影、饮食、关系、工作等跨域对话中的稳定性。
- `memory.activate` 同时观察与召回是否需要进一步拆成读写两个神经。
- 内心 Facet 每轮由 Brain 判断 changed 是否会出现过度更新或更新不足。

## 25. 部署形态：Host / WebUI / 可选身体

Brain + 外接插件这个**模式**来自 AstrBot。Unity 是第一台实验床，不是必须跟一辈子的宿主。老插件以后一点一点移，不整包搬。

### 25.1 三层必须分开

```text
Ubuntu 常驻 Host：Kernel、Brain Loop、Moment、持久约束
WebUI：只读观察 + 配置，不跑 Brain
外部进程插件：QQ、摄像头等，同一 Contribution 协议
Unity：本地实验室，或以后的形象/机器人身体客户端，不是服务器
```

```text
浏览器 / 手机浏览器 ──HTTP·WebSocket──► Soul Host
                                          ├─ Brain 与插件协议
                                          ├─ SQLite
                                          └─ 向量编码器
QQ / 摄像头 / 以后的 Unity 身体 ──Moment·Effector──► Soul Host
```

| 层 | 职责 | 现在 | 目标 |
|---|---|---|---|
| Soul Host | 24h 进程：Moment 循环、Brain、SQLite、插件启停、调度轮询 | Unity `MonoBehaviour` 面板在驱动 | Ubuntu 上的 systemd 服务 |
| Console | 看短卡、内心、日志、开关插件、改名字 | Unity IMGUI | 浏览器 WebUI，以后可再打 PWA |
| Body | 可选：3D 形象、机器人 | 和 Kernel 焊在一起 | 普通插件客户端 |

内核无论用 C# 还是 Python，都必须继续遵守本文底线：唯一 Brain、插件不能互调、时间只产运行事件、短卡常驻 / 习惯点亮。

### 25.2 内核语言：C# Host，禁止双写 Brain

已选定：Ubuntu 常驻进程是 C# `dotnet` 服务，入口在 `Tools/Host`。Unity 只当本地实验室。不要再开一套 Python Kernel。

Python 只给以后的外接平台插件（QQ、摄像头、老 AstrBot 插件）。内置身份 / 内心 / 时间 / 记忆 / 对话仍是 Host 进程内插件，语义上可关，不是 Kernel。

语言模型走 Host 上的 OpenAI 兼容路由：保存提供商 → 一键 `GET /models` → 选用当前模型。Brain 只看见 `ILlmClient`。

真正焊死 Unity 的只剩实验室里的 Sentis 与 IMGUI。Host 当前用占位向量编码器接通结构，BGE / ONNX 以后替换。

### 25.3 Host ↔ WebUI 能力清单（语言无关）

WebUI 不跑 Brain，不直接打开 SQLite，不替 Brain 调用神经。所有观察和配置都经过 Host。路径名是契约语义，实现时可换前缀，不能换含义。

**观察（只读，不触发 Brain）**

- `GET /status`：进程是否活着、当前会话、两人名字与称呼、Host 时间。
- `GET /identity/cards`：四张身份短卡（人格 / ta是谁 / 她是谁 / 我们）及 revision。
- `GET /inner`：当前内心切片（一句话 + mood 等字段）。
- `GET /plugins`：已发现插件、是否启用、贡献目录。
- `GET /moments`：近期 Moment 原文（条数受原始上下文上限约束）。
- `GET /turns/last`：最近一轮 BrainFrame、Brain 意图、能力回调、Facet 输出。
- `WS /events`：流式日志、新 Moment、Brain Step、静默后台事件。

**配置（写，不经过 Brain 编排）**

- `PUT /identity/pair`：保存两人名字与称呼。
- `PUT /identity/cards/{slot}`：手改一张短卡。
- `PUT /plugins/{id}/enabled`：启停插件。
- `PUT /settings/context-limit`：原始上下文条数。
- `GET /providers`、`PUT /providers/{id}`：OpenAI 兼容提供商（Base URL / Key / 模型）。
- `POST /providers/{id}/models`：一键获取模型列表。
- `PUT /providers/current`：选用当前提供商与模型。
- LLM 密钥只进 Host 本机 `llm-providers.json`，不进 SQLite、不进 WebUI 持久化、不进日志。

**进入生命（写，会触发 Brain）**

- `POST /moments`：投入一条对话 Moment，等价于当前 `dialogue.receive`。QQ 等真实外部感官可产生语义 Moment；时间到期和动作回执产生运行事件，均不走这条给「她打字」用的入口。

**WebUI 禁止**

- 直接改数据库。
- 直接 `call` 某个 CallableNerve / Effector。
- 替 Brain 写事实、认知或内心。
- 把完整人生网每轮倒进页面。短卡可以常驻展示；习惯与事实只在被点亮的那一轮出现在 `/turns/last`。

### 25.4 外部进程插件仍遵守同一协议

平台插件（Python 或其他语言）未来通过 JSON-RPC / WebSocket 接入 Host：

- 不能直接修改其他插件状态。
- 不能直接调用另一个插件。
- 只能返回 Result 或产生 Moment。
- 事实、认知和内心仍由对应领域插件的受控接口保存。

该远程插件通道尚未实现。实现时不得让外部进程取得 PluginManager。

## 26. 当前风险与后续工程问题

这些问题不改变总体架构，但需要后续解决：

1. **后台任务可靠投递**：当前轮询后即更新调度状态；未来应加入 ack / retry，防止进程在 Moment 落库前崩溃。
2. **多会话调度**：Schedule 已保存 ConversationId，宿主仍需完整支持多会话并发队列。
3. **插件权限**：摄像头、定位、发消息、机器人移动应有权限与副作用策略。
4. **取消与超时**：远程插件、Python 插件和设备动作需要统一超时、取消和幂等键。
5. **Prompt 预算**：MountedFacet 数量增加后，需要全局 token/字符预算与降级顺序。
6. **动态 Output Schema**：当前 Facet 使用通用字段列表；未来可引入更严格的 Schema 校验器。
7. **数据库接口隔离**：当前插件服务仍共享存储服务面；未来可拆成 KernelStore、MemoryStore、InnerLifeStore 等最小权限接口。
8. **认知写入效率**：当前一条能力调用处理一条认知，真实使用后再判断是否需要批量事务。
9. **Tag 退化**：需要检测重复 Tag、过宽 Tag、临时句子型 Tag 和长期不再激活的 Tag。
10. **观测 UI**：当前 Unity IMGUI 是本地实验室。目标控制台是浏览器 WebUI，只消费 §25.3 的 Host API；插件以后可以注册只读可视化块，但不能改变 Kernel 数据边界，也不能让 WebUI 变成第二个 Brain。

## 27. 架构验收底线

未来任何修改至少应满足：

1. Moment 在重型处理前保存。
2. Kernel 不认识具体事实、认知和内心字段。
3. 只有一个 Brain 拥有第一人称与跨能力编排权。
4. 插件不能直接调用插件。
5. 固定挂载不伪装成 Brain 工具调用。
6. 普通对话可以完全不调用记忆。
7. 后台事件可以静默完成。
8. 用户自述、共享场景、传感器事实和明确虚构不得混为一谈。
9. 事实与认知必须分开，认知必须能修正和追溯证据。
10. 第三层从人生中生长，不预装百科全书。
11. 向量只负责候选导航，不能独自决定最终分类和召回。
12. 时间调度只产生运行事件，不进入 Moment，也不替 Brain 复盘或表达。
13. 插件外部动作必须返回真实执行结果，并形成 ProducedEvent；只有有语义的真实发言进入 Moment。
14. 原始上下文条数为 0 时，旧对话原文不得偷偷注入。
15. 没有共享人生 Tag 时，不得用“最近记忆”兜底制造伪相关。
16. WebUI 与可选身体客户端不得绕过 Host 直接改 Kernel 状态。
17. Unity 不得再承担 24 小时宿主职责。

## 28. 最终心智模型

```text
                         ┌──────────────────────┐
外部平台 / 身体 / 时间 ─→│  事件分流器            │
                         └──────────┬───────────┘
                         Moment / operational_event
                                    ↓
                         ┌──────────────────────┐
                         │     BrainFrame       │
                         │ 人格/ta是谁/她是谁/我们 │
                         │ 内心 / 时间感 / 目录    │
                         └──────────┬───────────┘
                                    ↓
                         ┌──────────────────────┐
                         │    唯一的 Brain      │
                         └──────┬───────┬───────┘
                                │       │
                           finish│       │call
                                │       ↓
                                │  内部神经 / 表达器
                                │       │
                                │       └── Result ──→ 回到 Brain
                                ↓
                    Reply / Action / Silence
                                │
                                ↓
                    Facet 各自消费收尾输出
```

长期连续性不来自“每次都记住所有聊天”，而来自：

```text
不可替代的 Moment
+ 有证据的事实网
+ 持续演化的认知网
+ 此刻简洁而真实的内心切片
+ 唯一 Brain 每次重新作出的选择
```

这就是当前我们已经对齐的 TraceSoul2。

## 29. 平台与感官能力定位（2026-08-17 修订）

本节修订/补充 §8（插件架构）与 §25（部署形态），是「平台与感官架构定位讨论」的结论。展开见 `docs/PLATFORM_SENSORY_POSITIONING.md`。

**结论（三条大白话）：**

1. **身体自己会干的事，别来烦灵魂；干不了的，才交上来。**
2. **身体要随时说实话。** 状态（摄像头被挡、带宽下降、算力紧张）变了就报告，灵魂按身体此刻的真实状态决定把活交给谁，而不是按「你是哪个平台」。
3. **保命的开关不许讨价还价。** 急停这类安全能力是框架强制项，身体不许声明「我没有」。

**结构上**：灵魂（Brain/框架）定义统一的能力词汇表（有哪些维度、每维分几档），身体只填自己的实际值和当前状态；路由永远属于 Brain。之前讨论的「L0 转发 / L1 过滤 / L2 模式 / L3 自主」只是第 1 条的候选命名，**暂不定义，等第二个身体（机器人/摄像头）出现再定名落码**。

**与现状的关系**：统一贡献目录（`TraceContributionDescriptorData`）已是「统一能力表」；`IsAvailable(TraceTurnContext)` 已是「身体说实话」的雏形（只有有/没有两种状态）；平台注入四机制（`PlatformAdapters` / `ReplyChannelProviders` / `TurnCompleteHooks` / usage facet）已保证平台规矩平台自己带、拍板归 Brain。待实现：分级命名、状态降级、安全强制校验——全部暂缓。

**状态标记**：结论 = 已对齐；分级命名 / 状态降级 / 安全强制 = 待实现（暂缓，无第二个身体前不动工）。

## 30. 插件三层结构（2026-08-26 修订）

本节修订/补充 §8（插件架构）与 §25（部署形态），是「插件整合讨论」的结论。展开见 `docs/PLUGIN_LAYERS.md`。

**起因**：「万物皆可插拔」走到了头。内核（身份/内心/记忆/时间/感官目录/对话面）拔掉任何一个「她」都不成立，代码也早已强制启用、禁止关闭——它们是**内核组件**，不是插件；同时 `qq.*` 能力都长在 OneBot 这具身体上，与平台是父子关系而非平铺兄弟，但层级此前只存在于无人消费的 `PlatformId` 字符串里。

**结论（三层，各管一件事）**：

1. **内核组件**（identity / inner-life / memory / time / senses）：编译进主库、强制启用、不可关闭；实现 `ITracePlugin` 只是复用注册总线，不意味着可拆。文档与界面一律称「内核组件」。
2. **平台**（身体 = 连接桥 + 翻译，不做决策）：console（内置保底平台，平台身份但不可禁用——最后的对话面）、onebot（QQ）、game-session（自研游戏身体，由 Organ 升格为 Platform；其包内 profile——星露谷/通用游戏——概念上升格为其下器官，物理拆包渐进）。
3. **器官**（长在平台上的能力，独立开关）：`qq.status / qq.qzone / qq.tts / qq.imagegen` 隶属 onebot；`game.stardew / game.generic` 隶属 game-session；`PlatformId` 为空者为中枢器官，直属于灵魂。

**生命周期三规则（层级成为运行时约束）**：

1. 平台未启用或未连接 → 其器官休眠，Contribution 不进可用目录；休眠不是禁用，平台回来自动醒。
2. 平台启停联动子级：启用时等待中的隶属器官补激活，禁用时隶属器官集体休眠。
3. 列表按平台分组展示；内核组件单独一区，不提供开关。

**正式修订 §25.2**：「内置身份/内心/时间/记忆/对话……语义上可关，不是 Kernel」修订为：**它们是内核组件，语义上与代码上都不可关**；可关的是平台与器官。§9 的内置插件清单按此口径重新划分为「内核组件 + console 平台身份」；`builtin.dialogue` 拆为 console 平台（保底）与内核残余职责，整理时归位。

**与既有章节的关系**：§8 的五种 Contribution 回答「插件提供什么」，§29 的能力表与身体远近回答「路由怎么选」，本节回答「插件之间是谁的谁」。三个维度互不替代。

**状态标记**：三层定义与三规则 = 已对齐。休眠过滤（可用目录 / 上下文块 / 后台轮询 / Moment 入口）、启停联动（派生过滤，不改器官开关）、分组展示与休眠标记（`/plugins` + WebUI）、game.session 升格 Platform（含连接句柄）、console 平台身份与不可关保护 = **已实现**（2026-08-26，ChatCheck/CoreCheck 回归通过）；dialogue 内核残余拆分、星露谷/通用游戏物理拆包 = 待实现（渐进）。
