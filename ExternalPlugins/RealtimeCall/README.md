# 实时通话插件

当前版本提供一条可用的实时语音降级链路：Unity 或 QQ 音频网关负责麦克风、ASR、TTS、扬声器和回声处理；插件接收最终转写，交给 TraceSoul2 原有心智与记忆，再把回复文本推给客户端合成播放。它不是端到端 GPT Live，也不在 Host 内传原始 PCM。

2026-10-06 工作区新增本人声纹门槛与本地录入工具，尚未发布/部署。音频侧先做能量/VAD 筛选，再逐段校验声纹，通过后才调用 ASR；Host 再比对随转写携带的特征。静音、不匹配及播放回声不产生聊天事件。真实 QQ/Unity 音频网关仍须接入下面的 Python 模块，现有旧客户端不能直接获得声纹能力。

端点是 `/plugins/realtime-call/ws`，使用 `Authorization: Bearer <独立令牌>`。令牌至少 32 个非空白字符；默认空值会拒绝所有握手。外部客户端应使用 WSS，不能把令牌放在查询参数或分享链接里。

## 协议

只接受最大 64 KiB 的 UTF-8 JSON 文本（含声纹特征）。每个连接的 `seq` 必须为递增正整数；最近 128 条相同请求可安全重试。`conversation_id` 由服务端配置，客户端不能选择别人的对话。

```json
{"seq":1,"op":"hello"}
{"seq":2,"op":"start","transport":"unity"}
{"seq":3,"op":"transcript","session_id":"<id>","generation":0,"text":"最终 ASR 文本","voice":{"model":"speechbrain/spkrec-ecapa-voxceleb","playback_active":false,"windows":[{"speech_ms":1600,"embedding":["此处为同一音频提取的192个数值"]}]}}
{"seq":4,"op":"output_ready","session_id":"<id>","generation":0,"output_id":"<id>","duration_ms":1640}
{"seq":5,"op":"playback","session_id":"<id>","generation":0,"output_id":"<id>","played_ms":1640,"completed":true}
{"seq":6,"op":"interrupt","session_id":"<id>","generation":0,"reason":"manual"}
{"seq":7,"op":"ping"}
{"seq":8,"op":"end","session_id":"<id>"}
```

服务端会异步推送：

```json
{"op":"assistant_text","session_id":"<id>","generation":0,"output_id":"<id>","text":"要合成播放的回复"}
```

客户端合成后用 `output_ready` 报告实际音频时长，再递增报告 `playback`。未播放的回复不进入语义记忆；完整播放写入全文；先报告部分进度再 `interrupt`，只按时长比例保留已听到的文本前缀。打断会递增 `generation`，旧代次的晚到输出和回执会被拒绝。客户端不能凭空创建输出，只能确认服务端发出的 `output_id`。

`transport` 支持 `unity`、`qq_av_bridge`、`debug`。每个连接最多一场会话，同一对话只有一场活动通话。断连会归档已有播放前缀并清理内存会话。`hello` 会明确返回 `audio_ready=false`、`dialogue_ready=true`、`stage=transcript_bridge`，表示服务端对话就绪、音频仍由外部客户端处理。

`hello` 还返回 `voiceprint_required`、`voiceprint_status`（`enrollment_required/ready/disabled`）、`keepalive_seconds=10` 与 `silence_ends_call=false`。`idle_timeout_seconds` 检测的是网络失联；客户端应独立于麦克风、ASR 和 TTS 每 10 秒发送协议 `ping`。WebSocket 底层 ping 不替代这个 JSON 心跳。没有最大通话时长，已完成回执会回收，未确认播放的待处理输出仍有容量限制。

声纹失败返回 `ok=true, data.accepted=false, data.reason=...`，不进入心智、打断或记忆。会话/代次错误仍返回 `ok=false`。语音触发的 `interrupt` 同样须带 `voice`；只有用户按钮才发送 `reason=manual`。当前保守采用半双工：播放期间拒绝麦克风，尚无真正的 AEC，不能以此声称支持边听边说。

## 录入本人声音

音频网关使用独立 Python 环境（建议 Python 3.11），安装 `gateway/requirements.txt`。Host 无需 Python/PyTorch；首次提取特征时网关会下载官方模型，之后可使用本地缓存。不要把 Python 环境、模型或声纹放进源码包。

```sh
python -m pip install -r gateway/requirements.txt
python gateway/voice_filter.py record-enroll --output /持久数据/plugins_data/realtime-call/voiceprint.json
```

在连接麦克风的机器执行录入。录三段不同内容，每段八秒、至少五秒有效人声，录入时关闭电视。也可以用 `enroll --output <路径> first.wav second.wav third.wav` 输入三到八份单声道、16 kHz、PCM16 WAV，每份不超过十秒。仅显式 `--replace` 能替换已有声纹。若 Host 在服务器上，将生成的声纹文件放到该插件真实 `PluginDataDirectory` 的 `voiceprint.json`；网关本地也保留一份供前置检查。不要把文件放到会被更新替换的插件代码目录。

只保存模型标识、样本数量、人声时长和声纹向量，不另存原始音频。声纹也是敏感数据，仅保存在受限的持久目录，不上传 Git。删除文件即撤销录入，下一次输入会被拒绝；更换声纹时同步 Host 和网关副本。

```sh
python gateway/voice_filter.py check --profile /声纹目录/voiceprint.json test.wav
python gateway/voice_filter.py listen --profile /声纹目录/voiceprint.json
```

`listen` 只做麦克风匹配预览，不拨号、不转写、不保存音频。默认门槛 0.65 是未用本人录音校准的保守初值，不是准确率；先对照正常说话、轻声、远近距离、电视单独播放和同时说话的结果调节。命令的 `--threshold` 放在子命令前，网关值与 Host `voiceprint_threshold` 保持一致。

插件默认 `require_voiceprint=true`。未录入时保活与挂断仍可用，转写不会放行。旧网关必须升级；临时显式设为 `false` 才恢复未校验转写，也随之失去本人声音过滤。

## 网关接入与主动联系

`VoiceFilter.prepare_transcript(pcm, profile_path, transcribe, playback_active)` 接收一段最多十秒的 16 kHz 单声道小端 PCM16。客户端先按 VAD 分句，传入准确播放状态；函数对全部有效人声分为最多四窗，逐窗校验，任一不匹配就整句拒绝。通过后才调用传入的 ASR 函数，ASR 使用同一份已检查的 PCM；返回的 `text/voice` 和服务端会话、当前代次、递增序号一起发给 `transcript`。不要先转写所有环境声音，也不要用其他片段的特征给文本背书。

`keepalive(request, stopped)` 提供独立异步协议保活循环；`request` 由客户端负责原子分配序号、发送与等待回执。推理与 ASR 应放在工作线程，不能阻塞保活。该模块是网关输入组件，不含完整 WebSocket 客户端、QQ 设备驱动、ASR/TTS 供应商或回声消除。

Host 不接收原始音频，因此特征来自持有连接令牌的**受信任网关**，不是对恶意客户端的身份认证。声纹不能证明录音是真人实时发出的，也无法可靠分离同时说话的人；电视混音或短于 0.8 秒的回答可能被跳过。它不是声源分离或环境音降噪器。

私密对话需在宿主本人账号设置中绑定 `platform=realtime.call` 与插件配置的 `speaker_id`（默认 `voice-owner`），并在确认受众只有本人时开启 `private_call`。声纹匹配本身不会自动授予私密记忆权限；未确认受众或未绑定本人时不输出私密回复。入站和已播放回执带同一个服务端通话环境，旧通话及其他平台的轮次不能借当前连接发送。

收到本人第一句有效发言后，沿用原有主动联系调度与该通话目标；后台唤醒可自然开口，也可继续安静，不新增“每隔几分钟必须问一次”的计时器。持续通话状态仅进入动态提示：不填补沉默、不因无人回应连续追问、不从背景声音推测活动。尚未说过话的新通话不会凭空建立主动联系安排，通话中的主动联系仍受宿主原有开关/睡眠状态控制。

## Linux Docker / QQ

现有 OneBot/NapCat 消息接口不提供 QQ 电话音频。候选适配是社区 QQ AV Bridge；控制服务、QQ 和 PulseAudio 设备应留在受信任容器网络，不公开桥控制端口。音频网关在相同容器或网络命名空间读取 `maibot_qq_speaker.monitor`，把 ASR 最终文本发到本插件；收到 `assistant_text` 后把 TTS 音频播放到 `maibot_qq_mic`。

在 QQ/AV 桥容器内可先运行只读诊断：

```sh
python3 probe_qq_call_bridge.py \
  --token-file /实际桥接安装目录/runtime/control.token \
  --pulse-server unix:/实际桥接安装目录/runtime/pulse/native
```

脚本不输出 token、QQ 身份或原始状态，不安装、不修改 QQ，也不发起通话。前置检查通过仍不等于真实来电、主动拨出或双向音频已经验收。主动拨出和视频需要各自单独验证。

## 验证

```sh
dotnet run --project Tools/MediaRealtimeCheck/MediaRealtimeCheck.csproj
python -m unittest discover -s scripts/tests -p test_qq_call_probe.py
python -m unittest discover -s scripts/tests -p test_call_voice_filter.py
dotnet run --project Tools/ChatCheck/ChatCheck.csproj -- --realtime-call
```

离线检查覆盖 WebSocket 鉴权与限制、会话隔离、请求重放、最终转写、服务端推送、TTS 时长确认、完整/部分播放记忆、打断代次和断连清理；不会请求模型、连接 QQ 或拨打真人。

本轮 128 项媒体/通话、12 项 Python 检查、实际内核的通话环境/主动联系/播放记忆专项及完整 ChatCheck/Prompt 布局通过；Host 与插件构建通过。Python 用模拟特征验证流程，不代表模型实录准确率；本机尚未安装完整音频依赖、下载模型、录入本人声音或实测 QQ。ChatCheck 构建保留已有 CameraSharingContext 重复类型警告。

模型与接口依据：[SpeechBrain ECAPA 模型卡](https://huggingface.co/speechbrain/spkrec-ecapa-voxceleb)、[WebRTC VAD](https://github.com/wiseman/py-webrtcvad)、[sounddevice 录音接口](https://python-sounddevice.readthedocs.io/en/latest/usage.html)。
