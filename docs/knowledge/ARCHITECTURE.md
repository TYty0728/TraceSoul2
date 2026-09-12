# 当前架构地图

## 一轮对话

```text
QQ WebSocket → OneBotPlatformAdapter.ConvertInbound → OneBot 收件箱
本机输入 → SoulRuntime.ChatAsync
后台事件 → SoulRuntime.PollBackgroundAsync
                         ↓ 同一运行锁
KernelLogic → 保存输入 / 判断唤醒类型 / 识图 / 记忆预激活
            → MindLogic（组织当下、是否说话、是否出图）
            → 可选外出或工具 → ExpressorLogic（开口与附加表达映射）
            → MouthLogic 身体路由 → 平台适配器
            → 文字与表情合并发送 / 更新内心与轨迹 / 安排下一次心跳
                         ↓
生图立即后台生成 → DeferredTurnWork → SoulRuntime 轮后队列
                → 取得运行锁提交发送与回执入库
```

普通对话通常是 Mind + Expressor 两次 LLM；外出、识图、工具、画面规划、结构化响应重试会增加请求。**两次 LLM 请求并不等于发两条 QQ 回复**。

## 主要文件

| 文件或目录 | 负责什么 |
|---|---|
| `Tools/Host/Program.cs` | WebUI/API、WebSocket、运行时生命周期与配置入口 |
| `Tools/Host/SoulRuntime.cs` | 单角色运行、运行锁、后台轮询、轮后提交队列 |
| `Tools/Host/TraceHome.cs` | 软件与家目录分离，角色/插件路径解析 |
| `Tools/Host/ExternalPluginLoader.cs` | 外部程序集加载与共享契约解析 |
| `src/TraceSoul2/Logic/KernelLogic.cs` | 主编排、唤醒分流、发送、回执和轮后任务 |
| `Logic/MindLogic.cs`、`Logic/ExpressorLogic.cs` | 心智决策与外显表达 |
| `Logic/CommonContextPackLogic.cs`、`Logic/LlmContextPackLogic.cs` | 公共前缀、历史窗口和供应商策略路由 |
| `Prompts/CorePrompts.cs`、插件的 `*Prompts.cs` | 核心与器官提示词；不要在路由层散落提示词 |
| `Logic/MouthLogic.cs` | 身体/器官匹配与通道收口 |
| `Manager/TracePluginManager.cs` | 插件注册、启停、休眠、贡献目录和调用 |
| `Plugins/TracePluginContext.cs` | 服务、钩子、插件注册契约（编进 PluginApi） |
| `Plugins/Builtin/OneBotPlatformPlugin.cs` | QQ 连接、去重、收件箱、暂存、发送与输入状态 |
| `Plugins/Builtin/OneBotPlatformAdapter.cs` | OneBot 协议与规范消息互译 |
| `ExternalPlugins/QqImageGen/` | 相机提示、分享反馈、画面规划、参考图、生成和发图 |
| `Logic/HeartbeatLogic.cs`、`Plugins/Builtin/TimeSchedulerPlugin.cs` | 主动唤醒、睡眠与后续时间事件 |
| `Tools/Host/DailyPipelineWorker.cs`、`Tools/Migration/` | 日构建调度与处理管线 |

表中缩写 `Logic/`、`Plugins/`、`Prompts/`、`Manager/` 均相对于 `src/TraceSoul2/`。

## 数据与部署边界

- 主 SQLite：对话 Moment、运行事件、内心、身份、事实与索引等；向量 SQLite 由 `SqliteVectorManager` 管理。
- 图片、表情、调度等运行痕迹保存为 `OperationalEventRecord`，不混成真实聊天历史。相机反馈从这里读取成功发图回执。
- `MindLogic.Normalize` 当前清空普通轮的 `cognition`、`archive`；不要照旧交接文档恢复白天高频认知写入。
- PluginApi 是共享契约程序集，Host 与插件共享同一份类型；核心源清单显式维护。
- `TRACESOUL2_HOME` 放角色和全局配置，`TRACESOUL2_PLUGINS` 放代码包，`TRACESOUL2_PLUGINS_DATA` 放插件配置和图库。软件目录可以替换，角色与插件数据需要保留。
- 启动器、发布包和 Docker 可用不同路径。排障先确认实际启动脚本与进程，不从源码位置猜运行家目录。
