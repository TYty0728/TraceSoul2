# Prompt 装配约束

所有对话模型默认使用同一套公共上下文装配器。`BaseUrl` 识别只负责供应商传输兼容与缓存观测，不再决定是否启用稳定前缀：

| 渠道 | 入口 | 策略包要认的事 |
|---|---|---|
| DeepSeek 官方 | `api.deepseek.com` | 公共形状；缓存字段 `prompt_cache_hit_tokens` |
| Kimi 官方 | `api.moonshot.cn` / `moonshot.ai` / `api.kimi.com` | 公共形状；K3 回传 assistant `reasoning_content`；发 `prompt_cache_key`；缓存字段 `cached_tokens` |
| GLM 官方 | `open.bigmodel.cn` | 公共形状；`thinking.type`；缓存字段 `prompt_tokens_details.cached_tokens` |
| 中转及其他兼容模型 | 其他入口 | 公共形状；不假定供应商缓存字段或私有请求参数 |

`LlmContextPackLogic` 是策略路由入口，当前所有渠道都解析为 `Common`。以后某家需要特殊布局时，在路由层增加专用实现；心智、开口、复盘调用方不再出现供应商分支。

公共装配器按严格最长前缀匹配设计。即使供应商支持相似缓存，也不依赖相似度保证正确性：

```
[system] 公共身份卡（人格 / 我是谁 / 她是谁 / 我们 / 档案 / 表达习惯）
[对话历史]  user / assistant，两边字节相同
[相关记忆]  同一段预激活过去
[当前用户消息]  同一句（心跳没有这一条）
[心智 / 开口专属指令]  ← 唯一允许分叉的位置，必须在最后
```

Kimi 官网的心智、开口、复盘共用 `prompt_cache_key = tracesoul2:{conversationId}`。其他渠道沿用隐式缓存。Host 的 TimedLlmClient 必须转发 BaseUrl，供应商兼容层才知道是否添加私有字段。

一轮 Moment 会打两套请求，所有渠道都让它们共享同一段前缀：

1. **心智**：安静组织这一拍。不写台词，不看通道清单。
2. **外显**：带着心智的组织卡和共享预激活记忆开口。不看工具表，不重做决策。

两套请求都是同一形状：一条稳定 system、真正的 user / assistant 历史、共享记忆、当前原话、最后一条专属尾部。时间、内心、标签、此刻任务和输出协议等每轮动态内容不得再进入 system。

不要再把对话原文压成 `田园：… / 阿循：…` 塞进 system。不要把身份切成多条 system。

镜头就是 `user` / `assistant` 本身。专属尾部是明确标记的系统表达请求，模型真正要接住的仍是它前面的当前原话。

## 心智

公共 system 是同一套身份卡（含表达习惯）；思考规则、时钟、内心、标签、JSON 协议只出现在最后的【心智】。

输出是一张人能读完的组织卡：`beat`（当下 / 旧事 / 出门）、标签、心情、话题边界 `archive`、新识、出门事由、当前时 `inner`、在场注意 `attention`、给外显的 `note`（不是台词）。普通对话的 `review` 固定为 false；身份复盘由定点时间运行事件唤醒。

中枢按入口换轨：她说话走心智；`leave` 走代码外出链。定点「每日复盘」直达潜意识（现有 `identity.review`）；普通对话不派身份复盘。`archive` 只提供话题边界信号，代码在累计 40 条双方 Moment 后才允许小复盘，60 条时兜底强制执行。

## 外显

公共前缀与心智相同；开口格式、心智组织卡、表达请求只出现在最后的【开口】。不含 `【需要时可做的事】`，不填能力 ID。

主文字通道由宿主的 `ReplyChannelProvider` 确定；QQ 连着时感官目录不暴露控制台通道。附加表情、图片、语音由外显给内容，代码映射到 effector。

## 装配不变量

- 恰好一条 `role=system`。
- 不把 Moment 正文复制进 system。当前原话只出现一次，位于专属指令之前。
- 历史只含两人真实 Moment：人 → `user`，同伴 → `assistant`；排除 `system_event` 和出站 `[QQ` / `[CQ:` 占位。连续同一角色合并成一条。
- 不向模型展示 `callable_nerve`、`mounted_facet`、`unclassified`、`explicit_dialogue` 等实现枚举。
- `senses.catalog`、`qq.reply.channel` 与 `*.usage` 不拼进 Prompt；表达通道由代码映射。
- 第一段必须直接以第一人称身份开头，紧接人格卡；不得用“你是一个 Brain”之类框架角色抢占身份注意力。
- 记忆定位由心智勾标签，代码做向量拼装；不要为「总结给外显看」另开一轮 LLM。

## 尚未决定的历史策略

本次只统一装配入口，不引入 R0，也不改变最近历史的 `TakeLast(limit)` 行为。以下问题留待结合语义连续性一起决定：

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

该检查不访问外部 API，会验证所有渠道默认解析为 `Common`、心智与外显共用稳定 system、当前 Moment 位于专属尾部之前、对话原文不进 system、内部枚举隐藏、外显无工具表，以及 Kimi 的缓存键和 assistant reasoning 兼容项仍然生效。
