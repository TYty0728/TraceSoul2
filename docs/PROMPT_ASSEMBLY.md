# Prompt 装配约束

一轮 Moment 会打两套彼此独立的请求：

1. **心智**：安静组织这一拍。不写台词，不看表达习惯，不看通道清单。
2. **外显**：带着心智的组织卡和代码捞到的记忆血肉开口。不看工具表，不重做决策。

两套请求都是同一形状（对齐无插件 AstrBot 私聊）：

1. **一条 system**：身份、规则、本轮状态（时间、内心、标签、此刻任务、心智卡……）
2. **真正的 user / assistant 历史**：两个人说过的原话，按角色轮次排
3. **最后一条 user**：当前这句话，只出现一次

不要再把对话原文压成 `田园：… / 阿循：…` 塞进 system。不要把身份切成多条 system。

镜头就是 `user` / `assistant` 本身。最后一条 user 必须回的就是这个人刚说的话。

## 心智

唯一的 system 从「我是{assname}」进入思考用短卡（人格 / 我是谁 / 她是谁 / 我们 / 档案），然后是决策卡协议，再接本轮：当前时间、上一拍当前时、上一拍手上（工作台，换题不得照抄）、今日新识、今天轨迹、有上限的生命标签候选、与当前 Moment 向量预选后的情境模版（未命中则不注入）、外出结果（如有）、此刻任务。不含表达习惯、感官通道、`calls` 说明书。

输出是一张人能读完的组织卡：`beat`（当下 / 旧事 / 出门）、标签、心情、话题边界 `archive`、新识、出门事由、当前时 `inner`、在场注意 `attention`、给外显的 `note`（不是台词）。普通对话的 `review` 固定为 false；身份复盘由定点时间 Moment 唤醒。

中枢按入口换轨：她说话走心智；`leave` 走代码外出链。定点「每日复盘」直达潜意识（现有 `identity.review`）；普通对话不派身份复盘。`archive` 只提供话题边界信号，代码在累计 40 条双方 Moment 后才允许小复盘，60 条时兜底强制执行。

## 外显

唯一的 system 从「我是{assname}」进入完整短卡（含表达习惯），再接开口格式，然后是本轮：当前时间、心智组织卡、记忆血肉、外出结果（如有）、此刻。不含 `【需要时可做的事】`，不填能力 ID。

主文字通道由宿主的 `ReplyChannelProvider` 确定；QQ 连着时感官目录不暴露控制台通道。附加表情、图片、语音由外显给内容，代码映射到 effector。

## 装配不变量

- 恰好一条 `role=system`。
- 不把 Moment 正文复制进 system；当前原话只作为最后一条 user。
- 历史只含两人真实 Moment：人 → `user`，同伴 → `assistant`；排除 `system_event` 和出站 `[QQ` / `[CQ:` 占位。连续同一角色合并成一条。
- 不向模型展示 `callable_nerve`、`mounted_facet`、`unclassified`、`explicit_dialogue` 等实现枚举。
- `senses.catalog` 必须保留为自然语言表达通道清单，并靠近输出格式；`qq.reply.channel` 与 `*.usage` 不拼进 Prompt。
- 第一段必须直接以第一人称身份开头，紧接人格卡；不得用“你是一个 Brain”之类框架角色抢占身份注意力。
- 记忆定位由心智勾标签，代码做向量拼装；不要为「总结给外显看」另开一轮 LLM。

## 缓存观测

身份与本轮状态现在同在一条 system 里，跨 Moment 不再保证第一段 system 字节级相同。设置 `TRACESOUL2_LLM_DUMP_DIR` 后，每次请求除 `prompt.txt` / `response.txt` 外还会生成 `usage.txt`，其中包含：

- `prompt_cache_hit_tokens`
- `prompt_cache_miss_tokens`
- `prompt_cache_hit_rate`

## 回归检查

```powershell
dotnet build Tools\ChatCheck\ChatCheck.csproj
dotnet Tools\ChatCheck\bin\Debug\net8.0\ChatCheck.dll --prompt-layout
```

该检查不访问外部 API，会验证心智与外显各只有一条 system、最后一条 user 是当前 Moment、对话原文不进 system、带历史时形状为 `system / user / assistant / user`、内部枚举隐藏、外显无工具表、心智无表达习惯与通道清单。
