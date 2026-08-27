# TraceSoul2

Moment → 心智 → 外显 的纯 .NET 8 陪伴框架。心智是「我怎么想」，外显是「我怎么做」，复盘是「我长成什么样」。主工程不依赖 Unity。

插件不是平铺列表，是三层：**内核组件**（不可关）→ **平台 / 身体**（连接桥 + 翻译）→ **器官**（长在身体上的具体能力）。身体不在，器官休眠，不会假装还能发空间。见 [docs/PLUGIN_LAYERS.md](docs/PLUGIN_LAYERS.md)。

## 架构分层

```
内核组件（不可关）              身份 / 内心 / 记忆 / 时间 / 感官目录
        ↓
平台层（身体 = 连接桥 + 翻译）  console（观察窗）· OneBot/QQ · game.session
        ├─ QQ 器官              表情 / 语音 / 生图 / 说说 / 签名
        └─ 游戏器官             星露谷 / 通用游戏（目前在 game.session 包内）
        ↓ Moment
中枢 KernelLogic               按入口换轨：心智 / 外显 / 潜意识；出门走代码链
                               识图 → 心智；空闲抽一件生活事；夜间余烬；日构建
心智 MindLogic                 安静组织这一拍：当下 / 旧事 / 出门
外显 ExpressorLogic            带着决策卡和血肉开口，不重做决策
复盘 IdentityReviewLogic       短卡怎么长，不抢嘴
        ↓
记忆层                          四层多维索引 + 一句话总结 + 浸染细节 + 时间阶梯
```

- **内核**：身份短卡、内心、记忆、时间、感官目录编译进主库，强制启用。实现 `ITracePlugin` 只是复用注册总线，不是可拆插件。
- **平台**：只收发、只翻译，不做决策。`ITracePlatformAdapter` 负责平台消息 ⇄ Moment，并保证「已发送」事件入库完整。console 是观察窗 + 调试口，不可禁用，不参与身体滑落。
- **器官**：可独立开关。所属平台未启用或未连接时休眠（开关状态保留，平台回来自动醒）。
- **身体路由**：跨层近的压过远的（物理身体 → 自己的软件 → 文字聊天 → 控制台壳）；同层才打分。说话才改激活的身体，缺的器官才往更远的身体下滑。配置在数据目录 `bodies.json`。见 [docs/PLATFORM_SENSORY_POSITIONING.md](docs/PLATFORM_SENSORY_POSITIONING.md)。
- **空闲生活**：没人说话进入空闲时，系统按日限均匀随机抽一件事（发说说 / 看说说 / 改签名，或歇着），模型不选活动。
- **识图**：控制台「识图多模态」槽看她发来的图，看见的结果再交给心智；槽留空则只知道发了图，不假装看见。
- **记忆**：每日 04:00（+08:00，04:00 前归前一天）自动日构建：Moment → 事件索引+细节浸染 → 复盘六张卡/内心 → 当日排序。睡过 04:00 也会按墙钟补跑。阶梯榜 日→周→月→年→永久，每层 5 条，晋升=MOVE（跨层不重复）。

## 目录结构

| 路径 | 说明 |
|---|---|
| `src/TraceSoul2/` | 内核、数据契约、平台与插件运行时源码 |
| `ExternalPlugins/` | 本仓库内的器官/平台包源码（QQ TTS / 生图 / 说说 / 签名、game.session） |
| `models/` / `resources/` | BGE 模型与身份种子资源 |
| `Tools/PluginApi/` | `TraceSoul2.PluginApi` 共享契约（外部插件只依赖它，当前 1.2） |
| `Tools/Host/` | 常驻宿主（ASP.NET Core，控制台 5080） |
| `Tools/Migration/` | 迁移与日构建管线 |
| `Tools/ChatCheck/` | 内核与插件回归（不连真 API） |
| `Tools/KernelSources.props` | 宿主/迁移工具共用的内核源清单 |
| `docs/` | 架构总纲、插件三层、Prompt 装配、发布说明 |

Prompt 的分层、去重与缓存约束见 [`docs/PROMPT_ASSEMBLY.md`](docs/PROMPT_ASSEMBLY.md)。

## 家目录与软件分离

软件（本仓库或发布包）可以整份替换。角色、设置和已装插件都住在应用目录外的整合家目录，由 `TRACESOUL2_HOME` 指定。

正式安装推荐使用三个并列目录：

```
TraceSoul2\
  App\                   ← GitHub Release 软件包；更新时整体替换
  Data\                  ← TRACESOUL2_HOME：全局设置与所有角色
    home.json
    souls\<角色>\
    updates\
  Plugins\               ← TRACESOUL2_PLUGINS：独立插件代码包
  plugins_data\          ← TRACESOUL2_PLUGINS_DATA：插件配置与持久数据
```

发布包里的 `Start-TraceSoul2.cmd` 默认按这个布局启动：`App`、`Data`、`Plugins` 可以一起迁移，但软件更新只替换 `App`。

未使用发布包启动脚本时，简化的默认家目录结构是：

```
%TRACESOUL2_HOME%\
  home.json              ← 当前角色、控制台地址
  souls\
    xun\                 ← 循：两套 sqlite、短卡、供应商、OneBot、plugin-data
    xiaoxi\              ← 小汐，结构相同
  plugins\               ← 已装器官/平台代码包（dll + 默认资源）
  plugins_data\          ← 按包名分目录保存配置与运行数据
  updates\               ← 更新下载、校验与临时运行器
```

拷走 `Data` 即可迁移全部角色和设置；只拷 `souls/<角色>` 可以迁移单个角色。未设置环境变量时落到 `%LOCALAPPDATA%\TraceSoul2`。插件代码目录默认是家目录的 `plugins/`，也可用 `pluginsDirectory` / `TRACESOUL2_PLUGINS` 指向别处；数据目录默认为它的同级 `plugins_data/`，可用 `pluginsDataDirectory` / `TRACESOUL2_PLUGINS_DATA` 覆盖。

版本号在 `Tools/Directory.Build.props` 的 `TraceSoul2Version`。日常 commit 不改；决定集成一版时才运行 `scripts/Set-Version.ps1`、创建 `v*` 标签并生成 GitHub Release。已安装电脑可在 WebUI 检查并一键更新，详见 [版本与发布](docs/RELEASES.md)。PluginApi 在自己的 csproj 里单独编号。

## 运行

```powershell
dotnet build Tools\Host\TraceSoul2.Host.csproj

$env:TRACESOUL2_HOME = "$env:USERPROFILE\TraceSoul2Data"
dotnet Tools\Host\bin\Debug\net8.0\TraceSoul2.Host.dll
```

回归（不消耗 API）：

```powershell
dotnet run --project Tools\ChatCheck\ChatCheck.csproj
```

环境变量：`TRACESOUL2_HOME`（家目录）、`TRACESOUL2_DATA`（可选，覆盖当前角色路径，调试用）、`TRACESOUL2_URLS`、`TRACESOUL2_PLUGINS`（代码包）、`TRACESOUL2_PLUGINS_DATA`（配置与持久数据）、`TRACESOUL2_MIGRATE_DLL`。

控制台只接受本机回环连接和同源浏览器请求；不要把它反向代理到公网。Windows 系统代理指向本机 Clash 但 Clash 没开时，LLM 请求会打到空端口被拒绝——那是系统代理残留，不是模型或 Key 的问题。

生产运行时让启动脚本或服务设置 `TRACESOUL2_HOME`；不要把机器绝对路径写进仓库。密钥、Cookie、数据库只进家目录，提交前可跑 `pwsh scripts/Test-PublishSafety.ps1`。

每个角色目录内：`tracesoul2-brainframe.sqlite3`、`tracesoul2-vectors.sqlite3`、`llm-providers.json`、`onebot.json`、`bodies.json`、`memory-nerve.json`、`identity_cards.json`（种子）。插件数据不再混入角色目录，统一位于 `plugins_data/<包名>/`。

## 控制台（http://127.0.0.1:5080）

- **对话 / 实时状态 / 日志**：本机文字壳、链路、SSE。
- **身份**：名字、六张短卡、内心。
- **记忆**：记忆神经（top_k + 子代理）、日构建、阶梯榜、当天轨迹。
- **大脑 · LLM**：多种供应商同时在线；用途槽给灵魂和器官用——对话开口、思考、复盘、识图多模态、语音、生图。
- **平台 · QQ**：连接状态、OneBot 模式/端口/token、回发开关、NapCat 启动路径。
- **插件**：按平台分组列出器官；齿轮填配置；平台不在则显示休眠。
- **一起玩**：game.session 临时工作台（星露谷一键安装等）。
- **系统更新**：只装正式 GitHub Release；角色与插件目录不参与替换。

## QQ 平台（OneBot v11 / NapCat）

- 反向 WS 为主：宿主监听 `ws://127.0.0.1:9021/ws`，NapCat 主动连入，事件与 API 动作共用一根连接。
- NapCat 登录账号与启动脚本属于机器私有配置，不要写进仓库。
- 配置在数据目录 `onebot.json`（WebUI「平台 · QQ」保存即重启生效）：`enabled / mode(reverse|forward) / listen_port / ws_url / http_url / access_token(可多个) / self_id / reply_enabled / napcat_path`。保存本地 NapCat 的 `.exe/.bat/.cmd` 或安装目录后，可在 WebUI 点「启动 NapCat」；Host 重启不会自动重复拉起。
- QQ 私聊中，对话轮会在第一次 Mind LLM 请求前就通过 NapCat `set_input_status` 显示「正在输入」，心跳等自主轮次则在决定开口后显示；状态持续到本轮文字、表情、语音及轮后图片全部发送完毕。不发 `event_type=0`。群聊无对应的好友输入状态。
- 说说 Cookie 经 NapCat `get_cookies` 自动取，不手填；发布接口假失败只发一次、绝不重试。

## 外部插件

运行时从家目录 `plugins/` 加载（可用 `TRACESOUL2_PLUGINS` 覆盖），一个文件夹一个包。安装/卸载/更新都不需要编译宿主。平台未连接时，隶属器官休眠。

| 包 | 角色 | 功能 |
|---|---|---|
| `qq-sticker` | QQ 器官 | 情绪词 → 表情/GIF（源码可独立仓库） |
| `qq-tts` | QQ 器官 | 情感语音（OpenAI 兼容 speech） |
| `qq-imagegen` | QQ 器官 | 生图发 QQ；心智只决定发不发，画面规划在插件内 |
| `qq-qzone` | QQ 器官 | 发/看说说；空闲抽签；Cookie 自动取 |
| `qq-status` | QQ 器官 | 改签名/在线状态；空闲抽签 |
| `game-session` | 游戏平台 | 一起玩的临时工作台；原始事件不进主记忆 |

详见 [docs/PLUGINS.md](docs/PLUGINS.md)、[docs/PLUGIN_LAYERS.md](docs/PLUGIN_LAYERS.md)、[docs/GAME_SESSION_PLUGIN.md](docs/GAME_SESSION_PLUGIN.md)、[docs/PLATFORM_SENSORY_POSITIONING.md](docs/PLATFORM_SENSORY_POSITIONING.md) 与 [docs/ROADMAP.md](docs/ROADMAP.md)。
