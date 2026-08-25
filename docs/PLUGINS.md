# 插件体系（身体上的器官包）

插件不是内核。身份、内心、记忆、时间、控制台壳挂在贡献总线上，但**不是可关的插件**。控制台「插件」页按 **平台 → 器官** 分层：上层是 QQ 这类平台，下面才是表情 / 语音 / 生图 / 说说。

两类东西：

1. **内核 / 平台**（`src/TraceSoul2/Plugins/Builtin/`）：与宿主一起编译。内核不可关闭；QQ 平台的启用/回发在「平台 · QQ」。
2. **器官包**（`plugins/` + 同级 `plugins_data/`）：主项目之外，运行时加载，**安装/卸载/更新都不需要编译宿主**。

元数据 `Role`：`kernel` / `platform` / `organ`。`PlatformId` 标明器官属于哪座平台（QQ = `onebot`）。空则按 Id 推断。

## 运行时模型

- `plugins/<包名>/` 是可替换的代码包：`plugin.json`（manifest + 默认值）+ 程序集 + 默认资源。
- `plugins_data/<包名>/` 是持久数据：`config.json` + 生成文件 + 用户扩充资源。
- 每个包加载进**独立的可回收 AssemblyLoadContext**：坏插件只报错；卸载即释放；进程不崩。
- 程序集**从内存流加载**（不占文件句柄）：运行中直接覆盖 dll → 控制台「重新扫描」→ 热更新。
- `TraceSoul2.PluginApi` 共享契约回落默认上下文：插件与宿主共用同一份类型。
- WebSocket 入口用 `context.AddWebSocketEndpoint(...)` 注册；宿主按插件归属移除旧端点，并在每次新握手时解析当前实例，所以配置保存/重扫不会继续命中死对象。
- `PluginApi 1.1` 起包含上述 WebSocket 生命周期注册口；只使用旧贡献接口的插件无需改源码。
- 安装 = 丢代码文件夹；卸载只把代码移入 `plugins-uninstalled/`，对应 `plugins_data` 不删除。

## PluginApi 契约（外部插件只依赖它）

正式 Release 会同时附带 `TraceSoul2.PluginApi.<版本>.nupkg`。插件源码应引用这个稳定 SDK 包，不要引用宿主源码或机器绝对路径；插件自身可以放在完全独立的仓库中。PluginApi 版本独立于产品版本，只有契约变化时才升级。

`Tools/PluginApi/TraceSoul2.PluginApi.csproj`，与宿主共编同一批源文件（`PluginApiSources.props`），类型天然一致。包含：

- 数据 POCO：`MomentRecord`、`PairIdentity`、六张短卡、内心、轨迹、阶梯、事件索引、向量节点、`DeepSeekMessageData` 等；
- 接口：`ITracePlugin` / `ITraceMountedFacet` / `ITraceCallableContribution` / `ITraceMomentSource` / `ITraceBackgroundService`、`ITracePluginRegistrar`、`ITracePlatformAdapter`、`IMemoryStore`、`ILlmClient`、`IEmbeddingService`、`IMemoryRecallEngine`、`IHierarchicalVectorRouter`、`ITraceWebSocketEndpoint`；
- 上下文：`TracePluginContext`（含只读的 `PackageDirectory` 和可写的 `PluginDataDirectory`）、`TracePluginServices`、`TraceTurnContext`。

## 平台注入机制（器官包可用的宿主服务）

| 服务 | 用途 |
|---|---|
| `Services.PlatformAdapters` | 平台适配器注册表；器官通过它把表达发到平台（`SendAsync`），或调平台特有动作（`CallActionAsync`，如 get_cookies） |
| `Services.TurnCompleteHooks` | 整轮表达结束后的收尾钩子（QQ 用它把暂存文字与结尾表情合并成一条消息） |
| `Services.Embedding` | 文本语义向量（Host 侧 ONNX BGE），表情匹配等语义任务用 |
| `Services.Llm` / `NerveLlm` | 语言模型口 |
| `Services.Storage` | 记忆存取面（LoadPairIdentity 等） |
| `Services.LifeState` | 当前生活状态（位置 + 活动）的读写面；更新必须带 `source/source_id`，按来源优先级仲裁 |

例如游戏插件在 `ExecuteAsync` / Facet 中开始会话时只更新活动，不碰物理位置（`turn` 为当前轮上下文）：

```csharp
context.Services.LifeState?.Update(turn.ConversationId, new LifeStatePatchData
{
    activity = "游戏",
    activity_detail = gameTitle,
    source = LifeStateSourceValues.Plugin,
    source_id = sessionId
});
```
| `context.PackageDirectory` | 本包代码文件夹（只读 manifest/程序集/默认资源） |
| `context.PluginDataDirectory` | 本包持久数据目录（读写配置、生成文件和用户资源） |

出站落在哪具身体、哪只器官，由 `MouthLogic` 按激活身体 + 远近下滑收口。插件**不要**再声明回复通道，也**不要**往 Prompt 灌用法说明（`*.usage` 会被滤掉）。

**能力定位**：身体自己会干的事别来烦灵魂，干不了的才交上来；身体随时说实话（断连/缺配置 → 目录里自动消失）。详见 [PLATFORM_SENSORY_POSITIONING.md](PLATFORM_SENSORY_POSITIONING.md)。

## 写一个新器官包

```xml
<!-- TraceSoul2.Plugin.Xxx.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>TraceSoul2.Plugin.Xxx</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="<主项目>\Tools\PluginApi\TraceSoul2.PluginApi.csproj"
                      Private="false" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

```csharp
public sealed class XxxPlugin : ITracePlugin
{
    public TracePluginMetadataData Metadata { get; } = new TracePluginMetadataData
    {
        Id = "qq.xxx", DisplayName = "示例器官", Version = "1.0.0",
        Author = "你", Role = PluginRoleValues.Organ, Description = "……"
    };

    public void Register(TracePluginContext context)
    {
        context.AddCallable(new XxxEffector());
    }

    public void Shutdown() { }
}

public sealed class XxxEffector : ITraceCallableContribution
{
    public TraceContributionDescriptorData Descriptor { get; } = new TraceContributionDescriptorData
    {
        Id = "qq.xxx.send",
        Kind = TraceContributionKindValues.Effector,
        DisplayName = "示例器官",
        BodyId = BodyIds.Qq,
        BodyTier = BodyTierValues.Chat,
        Organ = BodyOrganValues.Image,
        ParametersJsonSchema = "{prompt:string}",
        HasExternalSideEffect = true
    };

    public bool IsAvailable(TraceTurnContext context) { return context != null; }

    public Task<TraceCapabilityResultData> ExecuteAsync(
        BrainCapabilityCallData call, TraceTurnContext context, CancellationToken cancellationToken)
    {
        var adapter = context.Services.PlatformAdapters.FirstOrDefault(a => a.PlatformId == "builtin.onebot");
        return adapter.SendAsync(new TraceOutboundMessageData { Kind = "image", File = call.GetArgument("file") },
            context, cancellationToken);
    }
}
```

贡献表请声明 `BodyId` / `BodyTier` / `Organ`。没声明时，内核仍按 id 猜测（`qq.` 开头算 QQ）。

其余约定：

- 出站表达**必须**经平台适配器并带 `ProducedEvent`；实际文字进入 `moments`，图片、表情、语音等动作回执进入 `operational_events`；
- 未配置（如缺 api_key）时让 `IsAvailable` 返回 false，该器官自动从身体上消失。

平台无关的器官（如 `game.session`）不要声明 `PlatformId` / `BodyId`，控制台会把它收在“独立 / 跨平台”；它的 Facet、后台服务和神经在 QQ、其它平台或独立壳上共用同一份状态。

## 已交付的四个包（家目录 `plugins/`）

| 包 | 插件 id | 功能 | 配置（plugins_data/<包名>/config.json） | 库 |
|---|---|---|---|---|
| `qq-sticker` | qq.sticker | 情绪词 → 语义/标签匹配 → 图片/GIF 发到 QQ | `threshold`（语义阈值）、`personas`（人格目录列表，默认 `_default`） | `emojis/<人格>/emoji_index.json` + 图片（兼容老 smartemoji 结构）；无图库时退回内置 face `stickers.json` |
| `qq-tts` | qq.tts | 要念的话 + 情绪词 → 情感语音（OpenAI 兼容 speech 接口） | `api_key`、`api_url`、`model`、`voice`、`audio_format`、`max_text_length`、`timeout` | 生成音频落 `plugins_data/qq-tts/generated/` |
| `qq-imagegen` | qq.imagegen | 画面关键词 + 可配置角色风格模板 → 生图发 QQ | `api_key`/`base_url`/`model`、`size` | 生成图片落 `plugins_data/qq-imagegen/generated/` |
| `qq-qzone` | qq.qzone | 全文 → 发一条机器人 QQ 空间说说 | 无需配置（Cookie 自动经 NapCat `get_cookies` 获取，p_skey 算 g_tk） | — |

插件源码使用独立仓库或独立源码目录。运行时包默认放在 `%TRACESOUL2_HOME%\plugins`，数据默认放在同级 `plugins_data`；可通过 `pluginsDirectory` / `pluginsDataDirectory` 或对应环境变量分别覆盖。

## 操作手册

- 安装：把包文件夹放进 `plugins\` → 控制台「重新扫描」（或重启宿主）。
- 更新：重新编译 → 直接覆盖包内 dll → 「重新扫描」。
- 卸载：控制台先停止插件，再把代码包移入 `plugins-uninstalled/<包名>-<时间戳>/`；`plugins_data/<包名>/` 保持不动，重新安装同名包后自动继续使用。
- 开关：控制台「插件」页勾选（持久化，重启后保持）。内核不能关。
- 坏包：加载失败只在包状态里报 `error`，不影响宿主与其它包。
