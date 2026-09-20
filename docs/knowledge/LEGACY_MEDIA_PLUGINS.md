# 旧媒体插件复用核对

2026-09-20：只读核对工作区 `otherProject/plugins/` 中的旧 AstrBot 源码，未运行旧服务、调用真实模型或验证平台在线解析。以下是代码能力，不代表已迁入 TraceSoul2。

## 已找到的基础

- `plugin_upload_astrbot_plugin_share/main.py`：分享链接/卡片识别、短链跳转与多平台解析。B站 `_parse_video` 返回标题、简介、作者、时长、热门评论和封面；抖音解析返回正文、数据、图文图片或视频封面。所查实现未提供视频下载、音轨/字幕提取及视频时序理解链路。
- `plugin_upload_astrbot_plugin_videocall/`：网页语音/视频通话、邀请/接听/拒接/结束接口、会话上下文衔接、摄像头截图、VAD、语音转文字、流式文字及逐句 TTS。
- 通话 `static/index.html` 的 `detectVoice` 仅在 `!state.processing` 时开始录音；`sendMsg` 先调用 `/api/stt_only`，再调用回复接口；`sendMsgStream` 按句请求 `/api/tts` 并排队播放。这是分轮语音链路，不能据 README 的“实时通话”认定支持全双工或说话打断。
- 摄像头在录音期间定时截图，随本轮问题提交；这是离散画面输入，不能认定已支持持续视频流理解。
- 旧 QQ 通话邀请发送网页链接，接听发生在网页；未据此确认 QQ 原生音视频通话接入。
- `plugin_upload_astrbot_plugin_speech_reco/main.py`：QQ 语音文件定位/下载、SILK 解码、FFmpeg 转换、转录请求，可作为语音消息输入迁移参考。

## 与当前项目衔接

- 当前 `src/TraceSoul2/Logic/VisionLogic.cs` 提供入站识图；`ExternalPlugins/QqTts` 提供 QQ 语音合成发送。它们不等同于视频理解和实时通话。
- 当前 PluginApi 提供 `Services.ContextPack`、平台适配器及插件 WebSocket 端点。迁移时应使用当前共享上下文与记忆入口，替换旧 AstrBot/CoreMemory 耦合；平台和器官不自行接管心智决策。
- 旧网页交互、格式兼容、解析器与音频处理是复用候选；不能直接把旧 Python 包放入 .NET 插件目录运行。

## 待验证与设计方向

- 视频分享：在旧链接解析基础上补充可访问媒体获取、带时间戳的画面/音轨或字幕处理；明确区分“只读到页面信息”和“已分析视频内容”。平台解析规则是否仍有效需逐个平台实测。
- 通话：复用旧网页作为交互基础，评估实时语音后端；保留“识别 → TraceSoul2 → 合成”的基础方案。接入前验证打断、取消、实际已播放内容与记忆一致性、延迟及断线恢复。
- 本轮没有实现或部署新插件，没有修改旧插件与运行配置。

## 用户确认的方向（2026-09-20）

- 产品能力收拢为“媒体理解”和“实时通话”两个插件，不再按旧插件的单项能力零散迁移。
- 媒体理解首批支持 B站、抖音分享链接，包含实际视频内容理解；旧解析器作为来源适配参考。
- 实时通话优先 QQ 原生电话；若接入成本或稳定性不合适，可以自建 Unity App。后续扩展实时视频。
- 建议让通话连接适配（QQ / Unity / 后续机器人）与语音后端独立；视频画面带时间戳交给媒体理解，通话维护会话时间线。图片、文件与实时画面可复用理解接口，但实时帧需要单独的过期、丢帧和延迟策略。

## QQ 原生电话调研（2026-09-20，未实机验证）

- NapCat 的语音通话 API 请求 [issue #1878](https://github.com/NapNeko/NapCatQQ/issues/1878) 标记为 `Closed as not planned`；不能把现有 OneBot 语音消息接口当作通话接口，也不能据此认定所有第三方路径不可行。
- 找到社区项目 [maibot-qq-voice-call](https://github.com/ClaudiaGardner/maibot-qq-voice-call) 与其 [接入文档](https://docs.mai-mai.org/manual/adapters/qq-voice-call)。仓库说明独立 NapCat AV Bridge 处理来电、接听和双向音频，依赖 Linux QQ、AVSDK、PulseAudio 等，并通过 QQ Loader Hook 启动独立 AV Host；对 QQ/NapCat 版本敏感。
- 此项目可作为音频接入桥的候选，不能直接视为 TraceSoul2 插件。已查资料主要说明接听来电，主动拨出、视频、目标环境兼容和实际稳定性仍待单独验证。
- [GPT-Live client delegation](https://developers.openai.com/api/docs/guides/live-delegation) 可接入现有 Agent。需验证人格、上下文和输出控制：Live 自身会组织说话，后端结果校验不等于逐字控制所有发言。其可用性、供应商连接和本项目适配尚未实测。
