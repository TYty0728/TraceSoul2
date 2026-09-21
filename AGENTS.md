# 项目工作约定

## 每次进入

1. 读 `docs/knowledge/README.md`（当前索引与状态）。
2. 查看 `git status --short`，必要时阅读已有 diff；保留用户的未提交修改。
3. 按需读 `docs/knowledge/ARCHITECTURE.md` 或 `docs/knowledge/QQ_RUNBOOK.md`，再定位源码。不要每次重读长篇历史总纲。
4. 任务完成后更新知识库中的行为、验证结果及尚未验证事项。知识库只记稳定事实和可验证状态，不堆聊天全文。

## 实现约束

- 当前是 .NET 8 项目；核心源码在 `src/TraceSoul2`，ASP.NET Core 宿主在 `Tools/Host`。
- 保持心智决策、外显表达、平台收发、器官能力的边界；相机决定镜头，心智决定是否分享。
- 不重写潜意识/身份复盘内部来解决普通对话问题。修改设计语义时对照 `docs/ARCHITECTURE_ALIGNMENT.md` 的历史与修订。
- `Tools/KernelSources.props` 和 `Tools/PluginApi/PluginApiSources.props` 是显式源清单；新增相关核心源文件要加入对应清单。外部插件通常使用 SDK 默认源文件收集。
- 插件共享类型只能来自 `TraceSoul2.PluginApi`。改共享契约时同时构建 Host 与受影响插件；仅构建 Host 不会更新已安装外部 DLL。
- Prompt 的身份、历史和稳定角色段保持稳定；计数、时间、回执等只放动态段。详见 `docs/PROMPT_ASSEMBLY.md`。
- QQ 发送及失败重试需要考虑是否已产生实际副作用；不为验收主动联系真人。
- 不把运行密钥、Cookie、数据库、聊天 dump、机器绝对路径提交到源码。
- 项目初期采用频繁提交和发布：完成当前任务并通过相应验证后，可提交、推送并发布补丁版本，无需每次另行确认。仅集成本次已完成的改动，保留工作区其它未完成修改；确认 Release 可供 WebUI 更新后再报告发布完成。

## 常用验证

```powershell
dotnet build Tools/ChatCheck/ChatCheck.csproj --nologo
dotnet Tools/ChatCheck/bin/Debug/net8.0/ChatCheck.dll
dotnet Tools/ChatCheck/bin/Debug/net8.0/ChatCheck.dll --prompt-layout
dotnet build Tools/Host/TraceSoul2.Host.csproj --nologo
dotnet build ExternalPlugins/QqImageGen/TraceSoul2.Plugin.QqImageGen.csproj --nologo
git diff --check
```

ChatCheck 使用模拟 LLM 和临时数据，不消耗真实 API。按改动选检查，不要把启动真实 Host 当作无副作用测试。
