# TraceSoul2

Moment → 心智 → 外显 的纯 .NET 8 陪伴框架。心智是「我怎么想」，外显是「我怎么做」，复盘是「我长成什么样」。主工程不依赖 Unity。

## 架构分层

```
平台层（连接桥，只收发）        NapCat/OneBot v11 反向 WS（9021）· 控制台文字
        ↓ 平台适配器（翻译，边界线）
感官层（翻译器）                QQ 文字/图片/表情/语音/生图/说说 · 控制台
        ↓ Moment（规范消息结构）
中枢 KernelLogic               主运转：按入口换轨（心智 / 外显 / 潜意识）；出门走代码链
心智 MindLogic                 安静组织这一拍：当下 / 旧事 / 出门
外显 ExpressorLogic            带着决策卡和血肉开口，不重做决策
复盘 IdentityReviewLogic       短卡怎么长，不抢嘴
        ↓
记忆层                          四层多维索引[1234层] + 一句话总结 + 浸染细节 + 时间阶梯
```

- **平台层**：连接桥只负责收发；**平台适配器**（`ITracePlatformAdapter`）负责平台消息 ⇄ 规范结构互译，并保证「已发送」事件入库完整。
- **感官层**：插件自带全部生成逻辑（表情匹配、TTS 映射、生图模板、qzone cookie/g_tk），Brain 只给关键词。
- **身体路由**：跨层近的压过远的（物理身体 → 自己的软件 → 文字聊天 → 控制台壳）；同层才打分。控制台是最低的文字壳，不算自己的软件。说话才改激活的身体，缺的器官才往更远的身体下滑。配置在数据目录 `bodies.json`。见 [docs/PLATFORM_SENSORY_POSITIONING.md](docs/PLATFORM_SENSORY_POSITIONING.md)。
- **记忆**：每日 04:00（+08:00，04:00 前归前一天）自动日构建：Moment → 事件索引+细节浸染 → 复盘六张卡/内心 → 当日排序；阶梯榜 日→周→月→年→永久，每层 5 条，晋升=MOVE（跨层不重复）。

## 目录结构

| 路径 | 说明 |
|---|---|
| `src/TraceSoul2/` | 内核、数据契约、平台与插件运行时源码 |
| `models/` / `resources/` | BGE 模型与身份种子资源 |
| `Tools/PluginApi/` | `TraceSoul2.PluginApi` 共享契约程序集（外部插件只依赖它） |
| `Tools/Host/` | 常驻宿主（ASP.NET Core，控制台 5080） |
| `Tools/Migration/` | 迁移与日构建管线（import/classify/build/promote-all/run-all） |
| `Tools/KernelSources.props` | 宿主/迁移工具共用的内核源清单 |

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
  Plugins\               ← TRACESOUL2_PLUGINS：独立插件包
```

发布包里的 `Start-TraceSoul2.cmd` 默认按这个布局启动：`App`、`Data`、`Plugins` 可以一起迁移，但软件更新只替换 `App`。

未使用发布包启动脚本时，简化的默认家目录结构是：

```
%TRACESOUL2_HOME%\
  home.json              ← 当前角色、控制台地址
  souls\
    xun\                 ← 循：两套 sqlite、短卡、供应商、OneBot、plugin-data
    xiaoxi\              ← 小汐，结构相同
  plugins\               ← 已装器官包（dll + 资源），覆盖后控制台重扫即可
  updates\               ← 更新下载、校验与临时运行器
```

拷走 `Data` 即可迁移全部角色和设置；只拷 `souls/<角色>` 可以迁移单个角色。未设置环境变量时落到 `%LOCALAPPDATA%\TraceSoul2`。插件目录默认是家目录的 `plugins/`，也可通过 `home.json` 的 `pluginsDirectory`（相对路径按家目录解析）或 `TRACESOUL2_PLUGINS` 指向独立的 `Plugins`。

版本号在 `Tools/Directory.Build.props` 的 `TraceSoul2Version`。日常 commit 不改；决定集成一版时才运行 `scripts/Set-Version.ps1`、创建 `v*` 标签并生成 GitHub Release。已安装电脑可在 WebUI 检查并一键更新，详见 [版本与发布](docs/RELEASES.md)。PluginApi 在自己的 csproj 里单独编号。

## 运行

```powershell
dotnet build Tools\Host\TraceSoul2.Host.csproj

$env:TRACESOUL2_HOME = "$env:USERPROFILE\TraceSoul2Data"
dotnet Tools\Host\bin\Debug\net8.0\TraceSoul2.Host.dll
```

环境变量：`TRACESOUL2_HOME`（家目录）、`TRACESOUL2_DATA`（可选，覆盖当前角色路径，调试用）、`TRACESOUL2_URLS`、`TRACESOUL2_PLUGINS`（可选，默认家目录 `plugins`）、`TRACESOUL2_MIGRATE_DLL`。

控制台只接受本机回环连接和同源浏览器请求；不要把它反向代理到公网。

生产运行时让启动脚本或服务设置 `TRACESOUL2_HOME`；不要把机器绝对路径写进仓库。

每个角色目录内：`tracesoul2-brainframe.sqlite3`、`tracesoul2-vectors.sqlite3`、`llm-providers.json`、`onebot.json`、`bodies.json`、`memory-nerve.json`、`identity_cards.json`（种子）、`plugin-data/`。供应商模板见 `Tools/Host/llm-providers.example.json`，不要把带 Key 的文件推进 Git。

## 控制台（http://127.0.0.1:5080）

名字/短卡/内心、LLM 参数（冷暖/采样）、记忆神经（top_k + 子代理）、日构建（手动触发）、平台连接与 QQ 配置（模式/端口/token/self_id/回发开关）、身体路由（情境/同层分数/当前激活/器官）、可插拔器官包（开关/重扫/卸载）、阶梯榜、当天轨迹、最近一轮链路、实时日志。

## QQ 平台（OneBot v11 / NapCat）

- 反向 WS 为主（AstrBot aiocqhttp 同款）：宿主监听 `ws://127.0.0.1:9021/ws`，NapCat 主动连入，事件与 API 动作共用一根连接。
- NapCat 登录账号与启动脚本属于机器私有配置，不要写进仓库；这里只要求它连接本机 OneBot WebSocket。
- 配置在数据目录 `onebot.json`（WebUI「QQ 平台配置」保存即重启生效）：`enabled / mode(reverse|forward) / listen_port / ws_url / http_url / access_token(可多个) / self_id / reply_enabled(回发开关)`。

## 外部插件

主项目之外的独立插件包（运行时从家目录 `plugins/` 加载，可用 `TRACESOUL2_PLUGINS` 覆盖），一个文件夹一个包。安装/卸载/更新都不需要编译宿主。已交付：QQ 表情包、QQ 语音（TTS）、QQ 生图、QQ 说说。

详见 [docs/PLUGINS.md](docs/PLUGINS.md)（插件体系与开发）、[docs/PLATFORM_SENSORY_POSITIONING.md](docs/PLATFORM_SENSORY_POSITIONING.md)（平台与感官架构定位）与 [docs/ROADMAP.md](docs/ROADMAP.md)（接下来能做）。
