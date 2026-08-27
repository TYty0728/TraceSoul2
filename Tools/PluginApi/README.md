# TraceSoul2.PluginApi

TraceSoul2 运行时插件的稳定契约（当前包版本 **1.2.1**）。

插件工程只应依赖本包，不要引用 Host，也不要把角色数据拷进插件源码仓库。运行时由宿主把这份共享程序集注入每个可回收的插件加载上下文。

## 1.2 起

- `ILlmContextAssembler`（`Services.ContextPack`）：插件专用对话模型必须走公共装配器，与心智/开口共享身份卡和历史前缀，只在专属指令处分叉。
- WebSocket 生命周期注册口：`context.AddWebSocketEndpoint(...)`；配置保存 / 重新扫描不会命中死对象。
- 插件角色：`kernel` / `platform` / `organ`。器官声明 `PlatformId`；所属平台不在则由框架休眠。

只使用旧贡献接口的插件不必改源码。包布局、manifest 与生命周期见主仓库的 `docs/PLUGINS.md` 与 `docs/PLUGIN_LAYERS.md`。
