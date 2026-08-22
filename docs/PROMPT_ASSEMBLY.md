# Prompt 装配约束

一轮 Moment 会打两套彼此独立的请求，各自都是三条消息，顺序不可改：

1. **心智**：安静组织这一拍。不写台词，不看表达习惯，不看通道清单。
2. **外显**：带着心智的组织卡和代码捞到的记忆血肉开口。不看工具表，不重做决策。

两套请求都是：

1. **可缓存 system 前缀**
2. **本轮 system 后缀**（时间、决策、血肉、此刻）
3. **user**：当前 Moment 原话，只出现一次

DeepSeek 缓存按从第 0 个 token 开始的公共前缀匹配，因此任何高频动态字段都只能出现在稳定前缀之后。心智和外显的前缀不同，但各自跨 Moment 的第一段必须相同。

## 心智

稳定前缀从「我是{assname}」进入思考用短卡（人格 / 我是谁 / 她是谁 / 我们 / 档案），然后是决策卡协议。不含表达习惯、感官通道、`calls` 说明书。

动态段放：当前时间、上一拍当前时、上一拍手上（工作台，换题不得照抄）、今日新识、今天轨迹、有上限的生命标签候选、与当前 Moment 向量预选后的情境模版（未命中则不注入）、外出结果（如有）、此刻任务。情境怎么组织不写进稳定前缀。

输出是一张人能读完的组织卡：`beat`（当下 / 旧事 / 出门）、标签、心情、话题边界 `archive`、新识、出门事由、当前时 `inner`、在场注意 `attention`、给外显的 `note`（不是台词）。普通对话的 `review` 固定为 false；身份复盘由定点时间 Moment 唤醒。

中枢按入口换轨：她说话走心智；`leave` 走代码外出链。定点「每日复盘」直达潜意识（现有 `identity.review`）；普通对话不派身份复盘。`archive` 只提供话题边界信号，代码在累计 40 条双方 Moment 后才允许小复盘，60 条时兜底强制执行。

## 外显

稳定前缀从「我是{assname}」进入完整短卡（含表达习惯），再接自然语言感官目录和极简 JSON（`reply/sticker/qzone/voice/image/mood`）。不含 `【需要时可做的事】`，不填能力 ID。

动态段放：当前时间、心智组织卡、记忆血肉、外出结果（如有）、此刻。决策卡和血肉不能进入第一段，否则破坏前缀缓存。

外显的叙述坐标固定为“我正在对你说、我正朝你行动”。动作、光影和括号可以保留，但不能把当前收信人写成“她/名字”，也不能切换成镜头外旁观“他和她”的舞台剧本。档案记忆中的第三人称只提供事实，进入 `reply` 必须还原成“你”。

主文字通道由宿主的 `ReplyChannelProvider` 确定；QQ 连着时感官目录不暴露控制台通道。附加表情、图片、语音由外显给内容，代码映射到 effector。

## 装配不变量

- 不把 Moment 正文复制进 system。
- 不向模型展示 `callable_nerve`、`mounted_facet`、`unclassified`、`explicit_dialogue` 等实现枚举。
- `senses.catalog` 必须保留为自然语言表达通道清单，并靠近输出格式；`qq.reply.channel` 与 `*.usage` 不拼进 Prompt。
- `time.context` 只能进入第二条本轮 system。内心、今日新识、轨迹、阶梯对心智来说在动态段手写注入；外显只通过心智组织卡看见结论，不把这些动态块放进稳定前缀。
- 第一段必须直接以第一人称身份开头，紧接人格卡；不得用“你是一个 Brain”之类框架角色抢占身份注意力。
- 记忆定位由心智勾标签，代码做向量拼装；不要为「总结给外显看」另开一轮 LLM。

## 缓存观测

设置 `TRACESOUL2_LLM_DUMP_DIR` 后，每次请求除 `prompt.txt` / `response.txt` 外还会生成 `usage.txt`，其中包含：

- `prompt_cache_hit_tokens`
- `prompt_cache_miss_tokens`
- `prompt_cache_hit_rate`

## 回归检查

```powershell
dotnet build Tools\ChatCheck\ChatCheck.csproj
dotnet Tools\ChatCheck\bin\Debug\net8.0\ChatCheck.dll --prompt-layout
```

该检查不访问外部 API，会分别验证心智与外显的三段结构、Moment 唯一性、内部枚举隐藏、外显无工具表、心智无表达习惯与通道清单，以及不同 Moment 的第一段完全一致。
