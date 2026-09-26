# 事件与行动驱动的 Agent 运行框架

2026-09-26：替代普通对话固定 `Mind → Expressor` 的实现。人格、记忆与后台整理继续存在，但不再按心理学命名强制拆成多个模型岗位。

## 当前主链

```text
输入/运行事件 → 真实历史、身份、当前状态、记忆预激活
              → AgentStep
                  finish + reply → 公共表达映射 → 平台发送
                  continue + actions → 执行能力 → 实际结果 → 下一 AgentStep
                  wait → 保留必要状态、结束当前处理
设备完成/失败/取消回执 → 运行事件 → 下一次 Agent 处理
```

- 普通资料充分的聊天一次结构化生成，同时提供正文和可选状态变化。`inner` 等状态字段不会被当成聊天发送。
- 需要资料或行动时继续调用同一个 Agent；查询结果可以改变判断，不只是供另一个模型润色。
- `refine=true` 才走专门表达加工，草稿和实际工具结果一并提供。`ExpressorLogic` 保留公共图片/表情映射、可选润色和夜间余温专项生成。
- 最多四轮行动、每步最多四项调用，第五步只能收口。结构化输出沿用最多一次纠正；纠正通过前不执行动作。
- 中间 reply 不发送、不入聊天；真实对话最终必须有正文，或已经通过本轮回复身体的语音/文字能力回应（含持续表达的受理）。后台事件允许独立动作或沉默。
- 运行事件放在动态上下文中明确标识，不伪装成对方的 user 消息。

## 能力目录与执行

`AgentLoopLogic` 读取当前可用贡献目录，不再依赖一次向量检索的两个长尾候选来决定能否使用工具。核心维护能力继续隔离：日构建、身份修订、定时器内部接口不会暴露给普通聊天。

显式行动使用 `GetAvailableActionCatalog`，保留同一身体的全部可用动作，不按器官类型只留一个赢家。默认文字和附件仍由原 `GetAvailableCatalog` / 身体路由选择出口。只有语音而没有文字通道的入站身体也能用声音完成回应。

- `memory.recall(query)`：复用现有记忆检索，明确区分候选证据和空结果，不新增记忆总结 LLM。
- `dialogue.recent_history`：目录可用时读取近期原文。
- 外部插件的可调用能力、语音、动作等按实际目录及参数说明使用；插件仍负责参数和设备约束。
- `execution.cancel(execution_id)`：只能请求停止当前会话的持续执行。请求停止与已确认停止分开。
- QQ 空间发布、签名修改继续经过原有授权闸门。
- 每项行动执行时再次核对可用性。`call_id` 在一轮内不能改指另一项操作；相同能力、目标和参数即使换 ID 也复用先前结果，避免重复副作用。此去重作用于本轮，不代替平台收件去重或跨重启幂等。
- `body_id` 必须与贡献绑定匹配。`group_id` 关联同一身体的多模态行动，不承诺跨能力同时开始。真正的音画/动作同步由身体提供组合能力实现。
- 行动按顺序分派，避免并发访问现有 SQLite/插件可变状态。插件可受理后返回 `running`，由设备继续执行，无需等待动作结束才处理后续输入。
- 图片与表情保留现有发送映射和图片轮后队列，不为普通文字增加模型调用。

## 持续语音、机器人、Live2D 的公共契约

PluginApi 源码版本 **1.5.0** 新增字段和类型，保留既有调用签名与状态字段名称。

`TracePluginManager.ExecuteAsync` 分配不可由模型指定的 `call.execution_id`，并传递受执行账本管理的取消 token。插件可同步返回 `success/failed`，或返回 `running/accepted`，随后调用：

```csharp
context.Services.Executions.Report(new TraceExecutionReceiptData
{
    ExecutionId = call.execution_id,
    Sequence = sequence,
    Status = "running", // 或 completed / failed / cancelled
    ProgressMs = actualPlayedMs,
    ConfirmedContent = actuallyPlayedPrefix,
    Summary = "设备确认的实际情况"
});
```

- 回执序号必须递增，时间进度不能倒退，终态不能被迟到回执复活。
- 接收取消 token 的驱动必须停止设备并回报 `cancelled`。取消请求后仍可能收到尾部进度，不能把请求立即当作设备已停止。
- 插件禁用或卸载时，管理器先请求停止其持续动作，再移除能力和关闭插件。
- `ConfirmedContent` 是实际播放/表达内容，不由模型计划或播放时间比例推算。仅生成正文、仅受理播放，都不代表已经说完。
- 持续执行的终态只产生一次带 `ConversationId` 的运行事件，交给宿主轮询处理，不直接写成真实聊天。
- 平台负责真实聊天/播放确认的持久化；现有实时通话的播放入记忆与代次打断协议保持原样，本次未强行迁移。
- 账本为有界进程内运行态，不支持跨重启恢复设备动作。平台回执与运行事件继续按各自机制持久化。

独立计算能力可以保持 `PlatformId` 为空，被不同身体复用；身体声明可执行的输出能力。现有 QQ TTS 等插件保持原归属，本次没有复制或改写设备驱动。

## 状态与后台整理

`AgentStepData` 继承旧 `MindDecisionData` 仅用于状态与插件 ABI 兼容，不代表独立的模型阶段。正文在 `reply`，动作在 `actions`，最终状态由原有状态面处理。

身份修订、日构建、长期认知整理继续由后台入口触发。保留心跳安排、睡眠、今日轨迹与相机提示钩子，无需每轮输出内心独白或长思考记录。旧 `MindLogic.DecideAsync` 留作兼容代码，生产对话使用 `AgentLoopLogic`。

## 验证与边界

专项入口：`dotnet Tools/ChatCheck/bin/Debug/net8.0/ChatCheck.dll --agent-loop`。

覆盖真实 Kernel 单次回复、状态与正文隔离、工具/记忆续推、空结果、执行失败、重复调用、未知能力、目标身体校验、可选润色、独立语音/动作、运行事件角色、回执顺序、跨会话取消限制和行动预算。使用模拟模型、模拟设备与内存 SQLite，不向真人发消息。

2026-09-26 验证：完整 ChatCheck 与 `--prompt-layout` 通过；Host、PluginApi 和七个外部插件（GameSession、MediaUnderstanding、RealtimeCall、QqImageGen、QqTts、QqQzone、QqStatus）构建均为零警告、零错误；`git diff --check` 通过。另验证同类多动作、无文字通道的语音身体及插件禁用时的取消。0.1.15 发布前独立检出排除认知网改动，Release 全解决方案、完整 ChatCheck、Prompt 布局、86 项媒体/通话及更新流程检查通过，Windows 发布包与 SHA-256 校验通过。真实 Docker 升级由发布 CI 验证；尚未部署用户服务器。

真实模型自然度、请求时延、设备打断延迟仍需实际环境验证。没有新增机器人驱动、Live2D 渲染器、原生流式音频模型或精确跨设备同步；提供它们可接入的运行契约与模拟执行闭环。
