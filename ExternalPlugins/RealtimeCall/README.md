# 实时通话插件

当前版本提供一条可用的实时语音降级链路：Unity 或 QQ 音频网关负责麦克风、ASR、TTS、扬声器和回声处理；插件接收最终转写，交给 TraceSoul2 原有心智与记忆，再把回复文本推给客户端合成播放。它不是端到端 GPT Live，也不在 Host 内传原始 PCM。

端点是 `/plugins/realtime-call/ws`，使用 `Authorization: Bearer <独立令牌>`。令牌至少 32 个非空白字符；默认空值会拒绝所有握手。外部客户端应使用 WSS，不能把令牌放在查询参数或分享链接里。

## 协议

只接受最大 16 KiB 的 UTF-8 JSON 文本。每个连接的 `seq` 必须为递增正整数；最近 128 条相同请求可安全重试。`conversation_id` 由服务端配置，客户端不能选择别人的对话。

```json
{"seq":1,"op":"hello"}
{"seq":2,"op":"start","transport":"unity"}
{"seq":3,"op":"transcript","session_id":"<id>","generation":0,"text":"最终 ASR 文本"}
{"seq":4,"op":"output_ready","session_id":"<id>","generation":0,"output_id":"<id>","duration_ms":1640}
{"seq":5,"op":"playback","session_id":"<id>","generation":0,"output_id":"<id>","played_ms":1640,"completed":true}
{"seq":6,"op":"interrupt","session_id":"<id>","generation":0}
{"seq":7,"op":"ping"}
{"seq":8,"op":"end","session_id":"<id>"}
```

服务端会异步推送：

```json
{"op":"assistant_text","session_id":"<id>","generation":0,"output_id":"<id>","text":"要合成播放的回复"}
```

客户端合成后用 `output_ready` 报告实际音频时长，再递增报告 `playback`。未播放的回复不进入语义记忆；完整播放写入全文；先报告部分进度再 `interrupt`，只按时长比例保留已听到的文本前缀。打断会递增 `generation`，旧代次的晚到输出和回执会被拒绝。客户端不能凭空创建输出，只能确认服务端发出的 `output_id`。

`transport` 支持 `unity`、`qq_av_bridge`、`debug`。每个连接最多一场会话，同一对话只有一场活动通话。断连会归档已有播放前缀并清理内存会话。`hello` 会明确返回 `audio_ready=false`、`dialogue_ready=true`、`stage=transcript_bridge`，表示服务端对话就绪、音频仍由外部客户端处理。

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
```

离线检查覆盖 WebSocket 鉴权与限制、会话隔离、请求重放、最终转写、服务端推送、TTS 时长确认、完整/部分播放记忆、打断代次和断连清理；不会请求模型、连接 QQ 或拨打真人。
