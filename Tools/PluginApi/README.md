# TraceSoul2.PluginApi

TraceSoul2 运行时插件的契约（源码 **1.9.0**；已发布产品 **0.1.19** 使用 1.7.0）。正式资产状态见 [版本说明](../../docs/RELEASES.md)。

## 1.9 起

认知增加来源环境、可见范围、参与者来源与身份用途；身份卡增加来源、人工固定与认知版本引用；Agent 增加可选有限 `affect`。新增 `RuntimeSliceRecord` / `RuntimeDayReviewRecord`，供白天追加切片、夜间落地拼图。没有新增 `IMemoryStore` 必须实现的方法，SQLite 通过核心实现提供切片与版本事务。Host 与官方插件已一起重建；未发布。见 [完整存在实施](../../docs/EXISTENCE_IMPLEMENTATION.md)。

## 1.8 起

新增 `EnvironmentObservationData` / `EnvironmentSnapshotData`、`TraceTurnContext.Environment`、事件和持续执行的环境来源，以及 `SupportsPublicEnvironment` 能力声明。原构造器和执行注册签名保留重载；旧插件默认只在私密环境启用。平台须报告结构化身份/受众并按轮次目标发送，器官须核对数据可见范围后才声明公开支持。宿主和官方插件应一起重建；当前源码未发布，详见 [在场环境](../../docs/RUNTIME_ENVIRONMENT.md)。

## 1.7 起

独立模型 DTO `AgentOutputData`/`AgentActionData` 映射为内部 `AgentStepData`；模型不再读取旧 Mind 的 speak、归档或工具字段。`AgentStepData` 新增可选 `goal_updates` 与 `AgentGoalUpdateData`，表示明确偏好、当下和未来目标的创建/修订/完成/撤回；没有新增 `IMemoryStore` 必须实现的方法。缺失 Agent `step` 不再默认 finish，须显式选择。宿主与官方插件应一并重建，共享程序集随宿主提供。行为与存储边界见 [当下与未来目标](../../docs/GOAL_MEMORY.md)。

## 1.6 起

认知增加四领域、完整理解、证据 Moment 列表、关联操作与确认/挑战时间；新增可选 `ICognitionGraphStore` 读取接口，不增加 `IMemoryStore` 必须实现的成员。Agent 的 `attention_links` 仅可引用本轮召回的认知，runtime 保存来源引用。旧库增量迁移，见 [四领域拼图](../../docs/COGNITION_PUZZLE.md)。

## 1.5 起

新增 `AgentStepData`、`TraceExecutionRegistry` 与执行回执。能力调用增加 `body_id/group_id/execution_id`，结果增加 `ExecutionId`；既有调用签名保持兼容。持续语音、动作与取消的接入示例见 [Agent 运行框架](../../docs/AGENT_HARNESS.md)。

## 1.4 起

新增媒体来源/证据/理解结果与通话会话/播放回执数据契约。只添加数据类型，不改已有接口；媒体与通话插件需要配套 1.4 Host。不把控制连接就绪等同于音频或模型就绪。

插件工程只应依赖本包，不要引用 Host，也不要把角色数据拷进插件源码仓库。运行时由宿主把这份共享程序集注入每个可回收的插件加载上下文。

## 1.3 起

新增 `TracePluginServices.MindTurnPromptAppends`：器官向心智动态段提供本轮状态。Host 0.1.8 与本次官方相机插件一起升级；旧版 SDK 没有该成员。身份 system 和稳定角色段不受本轮计数影响。

## 1.2 起

- `ILlmContextAssembler`（`Services.ContextPack`）：插件专用对话模型必须走公共装配器，与心智/开口共享身份卡和历史前缀，只在专属指令处分叉。
- WebSocket 生命周期注册口：`context.AddWebSocketEndpoint(...)`；配置保存 / 重新扫描不会命中死对象。
- 插件角色：`kernel` / `platform` / `organ`。器官声明 `PlatformId`；所属平台不在则由框架休眠。

只使用旧贡献接口的插件不必改源码。包布局、manifest 与生命周期见主仓库的 `docs/PLUGINS.md` 与 `docs/PLUGIN_LAYERS.md`。


1.10.0 增加 `TracePluginServices.EventCompletedHooks`：有效事件的本轮发送（含延迟图片）完成、或处理提前返回/异常时通知。用于释放平台在收件时持有的状态；钩子异常不影响主处理。既有表达开始/完成钩子保留。
