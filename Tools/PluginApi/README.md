# TraceSoul2.PluginApi

TraceSoul2 运行时插件的契约（**1.7.0**，配套产品 **0.1.19**）。正式资产状态见 [版本说明](../../docs/RELEASES.md)。

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
