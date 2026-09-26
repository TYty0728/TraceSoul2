# Prompt 装配约束

所有对话模型默认使用同一套公共上下文装配器。`BaseUrl` 识别只负责供应商传输兼容与缓存观测，不再决定是否启用稳定前缀：

| 渠道 | 入口 | 策略包要认的事 |
|---|---|---|
| DeepSeek 官方 | `api.deepseek.com` | 公共形状；缓存字段 `prompt_cache_hit_tokens` |
| Kimi 官方 | `api.moonshot.cn` / `moonshot.ai` / `api.kimi.com` | 公共形状；K3 回传 assistant `reasoning_content`；发 `prompt_cache_key`；缓存字段 `cached_tokens` |
| GLM 官方 | `open.bigmodel.cn` | 公共形状；`thinking.type`；缓存字段 `prompt_tokens_details.cached_tokens` |
| 中转及其他兼容模型 | 其他入口 | 公共形状；不假定供应商缓存字段或私有请求参数 |

`LlmContextPackLogic` 是策略路由入口，当前所有渠道都解析为 `Common`。以后某家需要特殊布局时，在路由层增加专用实现；心智、开口、复盘与器官插件的专用对话调用都不要再写供应商分支。

公共装配器按严格最长前缀匹配设计。即使供应商支持相似缓存，也不依赖相似度保证正确性。内容按稳定性从前到后排布，越早变化的内容越靠后：

```
[system] 公共身份卡（人格 / 我是谁 / 她是谁 / 我们 / 档案 / 表达习惯）——跨轮字节稳定
[对话历史]  user / assistant，量化对齐窗口（见下），两边字节相同
[角色稳定段]  ← 分叉点（如【心智】【开口】【画面】）：思考方式 / 表达姿态 / 视角坐标，跨轮字节稳定
[相关记忆]  同一段预激活过去；每轮检索，不保证心智与开口一致，不占前缀位
[轮内动态段]  时间感、心智组织卡、leave/qzone 结果、此刻与表达请求——每轮都变，紧邻队尾
[当前用户消息]  同一句（心跳没有这一条）
```

历史窗口是量化对齐的滑动窗口，定参是**最高条数 Max**和**滑动条数 Align**（默认 9 / 4）。下限 `Min = Max - Align + 1`，窗口长度在 `[Min, Max]` 之间浮动；起点按全部对话条数对齐，攒满 Align 条才整体前移。Align=1 则每轮固定 Max 条。逐条滑动会让 system 之后的第一条历史每轮都变，前缀匹配当场断裂；对齐后相邻轮次的历史前缀字节级一致。这两项在「大脑 · LLM」里可改，保存在当前角色的 `runtime-settings.json`。旧字段 `contextInjectionCount` 是窗口下限，读入时迁成 `Max = Min + Align - 1`。

执行类指令（此刻怎么想 / 这次只从这里开口 / 表达请求）全部留在轮内动态段，紧邻当前用户消息——那是近因注意力最强的位置；前移的只有身份姿态类内容，它们与 system 人设同质，不依赖临场位置。

器官插件调用专用对话模型时（例如 QQ 相机的画面规划）必须走 `ILlmContextAssembler`，只更换专属指令头。不要把身份卡和近几句重新截断塞进一条 user，也不要把对话压成 `田园：… / 阿循：…`。

Kimi 官网的心智、开口、复盘与同模型的插件调用共用 `prompt_cache_key = tracesoul2:{conversationId}`。其他渠道沿用隐式缓存。Host 的 TimedLlmClient 必须转发 BaseUrl，供应商兼容层才知道是否添加私有字段。

2026-09-26 起，普通 Moment 使用 `【Agent 当下】` 一次生成正文与可选状态；需要能力时执行并带结果继续，`refine=true` 才另行调用 `【开口】`。见 [AGENT_HARNESS](AGENT_HARNESS.md)。

各次请求共用一条稳定 system、量化对齐的真实历史和稳定指令。时间、当前状态、实际能力目录、行动结果与设备回执放动态段。结构化协议规则放稳定段；本轮数据不得进入身份 system 或稳定角色段。后台事件不以对方原话的形式追加到末尾，而是在动态段明确标识为运行事件。

不要再把对话原文压成 `田园：… / 阿循：…` 塞进 system。不要把身份切成多条 system。

镜头就是 `user` / `assistant` 本身。专属指令说明这一轮要干什么；当前原话放在最后，模型接住的是这一句。

## Agent 与兼容心智上下文

器官稳定说明使用 `MindPromptAppends`，本轮回执/计数等使用 `MindTurnPromptAppends`，后者只进入动态段。相机的最近发图反馈不可放进身份 system 或稳定角色段。

公共 system 保留同一套身份卡（含表达习惯）。Agent 协议与器官稳定说明在稳定段；当前状态、能力目录及执行记录在动态段。输出 `step/reply/actions/refine`，沿用 `MindDecisionData` 的可选状态字段供插件和存量快照兼容，不再强制先生成组织卡。

旧 `MindLogic.DecideAsync` 保留兼容与专项检查，普通对话不再调用它。旧 `beat=出门` 不驱动固定外出链，查询/行动统一通过能力调用；长期归档与身份修订继续由完整日终管线触发。

## 外显

公共前缀与 Agent 相同；显式加工时提供正文草稿和实际行动结果。普通直接回复只使用公共表达映射，不调用开口模型。夜间余温专项生成仍保留独立入口。

主文字通道由宿主的 `ReplyChannelProvider` 确定；QQ 连着时感官目录不暴露控制台通道。附加表情、图片、语音由外显给内容，代码映射到 effector。

## 装配不变量

- 恰好一条 `role=system`。
- 不把 Moment 正文复制进 system。当前真实原话只出现一次，位于专属指令之后。
- 历史只含两人真实 Moment：人 → `user`，同伴 → `assistant`；排除 `system_event` 和出站 `[QQ` / `[CQ:` 占位。连续同一角色合并成一条。
- 插件专用对话模型（画面规划等）走同一装配器；分叉只允许出现在专属指令。
- 不向模型展示 `callable_nerve`、`mounted_facet`、`unclassified`、`explicit_dialogue` 等实现枚举。
- `senses.catalog`、`qq.reply.channel` 与 `*.usage` 不拼进 Prompt；表达通道由代码映射。
- 第一段必须直接以第一人称身份开头，紧接人格卡；不得用“你是一个 Brain”之类框架角色抢占身份注意力。
- 记忆先由代码预激活，Agent 可按缺口调用 memory.recall；不要为「总结给外显看」另开一轮 LLM。

## 尚未决定的历史策略

最近历史已改为量化对齐窗口（起点按 4 条对齐滑动），仍不引入 R0。以下问题留待结合语义连续性一起决定：

- 长时间间隔（例如半天）后是否直接切断上一段原始对话。
- 已有小复盘、当天轨迹和内心实时状态能否替代 R0。
- 新段开始时是否只选约 3 条原始对话作为语义基底，而不是携带完整旧窗口。
- 自然话题边界、时间断层和 token 预算谁拥有最高切段优先级。

## 缓存观测

公共 system 跨 Moment 保持字节级相同；身份复盘真正修改身份卡时允许它低频失效。设置 `TRACESOUL2_LLM_DUMP_DIR` 后，每次请求除 `prompt.txt` / `response.txt` 外还会生成 `usage.txt`。控制台「LLM#n 请求完成」同一行也会打出命中。

各家字段名不同，解析口在 `LlmUsageLogic`。没上报缓存字段时写「未上报」，不要写成 0%。

- DeepSeek：`prompt_cache_hit_tokens` / `prompt_cache_miss_tokens`
- Kimi 官网：`cached_tokens`；请求侧发同一把 `prompt_cache_key`（`tracesoul2:{conversationId}`）。K3 是最长前缀匹配，不是按段识别。
- GLM 官网：`prompt_tokens_details.cached_tokens`

## 回归检查

```powershell
dotnet build Tools\ChatCheck\ChatCheck.csproj
dotnet Tools\ChatCheck\bin\Debug\net8.0\ChatCheck.dll --prompt-layout
```

该检查不访问外部 API，包含 Agent 专项回归，并保留旧心智/外显兼容布局检查；验证所有渠道默认解析为 `Common`、共用稳定 system、当前真实原话位于专属指令之后、对话原文不进 system，以及缓存键和 assistant reasoning 兼容项。`--agent-loop` 可单独运行新主链检查。
