# 微雪感官终端：最小 USB 输出闭环

2026-10-19：新增表情＋状态＋底部字幕的320×480画面渲染、独立纯预览模式和显式单帧显示接口。运行`python bridge.py --preview-only --http-port 5090`可在无USB/ASR环境编辑预览；20像素字幕自动分页，当前手动翻页，尚未联动真实通话。详见[画面与验证边界](../../docs/WXXZ_DISPLAY.md)。

2026-10-17新增外部TTS播放：`/api/speak`支持Host传入PCM16 WAV，转48kHz单声道并按8秒分块，逐块完成确认；无`data`时保留原系统声音。远端固定句已取得真实板端回执，Host可选远端/本地千问配置与边界见[角色语音](../../docs/WXXZ_SPEECH.md)。

2026-10-17：新增0.3.0实时麦克风流、本地Silero VAD、CAMPPlus声纹特征，以及Host「开始/结束语音通话」。正在真机联调，当前验收基线仍见下方0.2.0手动听写记录。模型、录入/匹配边界与操作见 [语音通话](../../docs/WXXZ_VOICE_CALL.md)。

用于 **ESP32-S3-Touch-LCD-3.5（ST7796 SPI，非 3.5B）**。复用微雪的显示、电源和 ES8311 音频驱动，以电脑上的本地控制页发送中文、图片和短 WAV 音频，开发板返回执行结果。

这是可独立运行的硬件联调工具，当前通过 USB 连接电脑。2026-10-16 新增 Host 本地 WXXZ 主链、`/api/speak` 系统测试语音和固件 0.2.0 的限时麦克风采集/本地听写，见 [本地主链联调](../../docs/WXXZ_LOCAL_RUNBOOK.md)。桥本身不启动 Host、不调用云模型；Wi-Fi、摄像头上行、人脸识别和视频播放尚未接入。真实人声听写→主体回复→屏幕及播音的单轮闭环已由用户现场确认。

## 使用

安装 Python 3.12/3.13 和 `requirements.txt` 到独立环境。在烧录本目录固件的板子连上 USB 后运行：

```powershell
python bridge.py --port COM3
```

打开 `http://127.0.0.1:5089`，先点「检查连接」，再发送文字、选择图片或播放音频。实际 COM 号以设备管理器为准；后台运行可用：

```powershell
./Start-Panel.ps1 -RuntimeDirectory <仓库外运行目录> -Python <虚拟环境python.exe> -Port COM3 -OpenBrowser
./Stop-Panel.ps1 -RuntimeDirectory <同一运行目录>
```

- 固件上电即显示中文欢迎页，电脑发送的画面替换欢迎页；重新上电回到欢迎页。画面不写入闪存。
- 文字在电脑使用本机中文字库排版，图片等比例缩放为 320×480 画面，按大端 RGB565 发送。字体缺失可用 `--font` 指定。
- 音频接受最长 10 秒、16 位 PCM、单/双声道 WAV，电脑转换为 48 kHz 单声道 PCM16。首次验证可点「播放测试音」。音量当前固定为较低的 45。
- 回执表示显示 SPI 传输完成或音频驱动写入并等待尾音排空；不能代替人眼/耳确认实际画面、方向、颜色和声音。
- 控制页中的图片是电脑渲染预览，不是板子截屏。界面只在收到 `done` 后显示完成。
- 服务仅监听本机，拒绝外站 Origin、非本机 Host 及非 JSON 写请求。上传原件只留在内存，不自动保存。

## 固件构建与恢复

已使用 ESP-IDF **v5.5.1**、ESP32-S3 工具链和 `esp_codec_dev` **1.3.4**。Windows 可运行：

```powershell
./Build-Firmware.ps1 -IdfPath <ESP-IDF目录> -ToolsPath <IDF工具目录> -PythonEnvironment <IDF虚拟环境目录> -BuildDirectory <仓库外构建目录>
```

也可在配置好的 ESP-IDF 环境、`firmware` 目录中运行 `idf.py build`。生成物与下载依赖不提交。固定依赖见 `firmware/dependencies.lock`；驱动来源和许可证见 [THIRD_PARTY.md](THIRD_PARTY.md)。

首次烧录前，确认型号和 16MB Flash，读取完整恢复备份并保存 SHA-256；不要擦除 eFuse 或启用安全启动。烧录使用构建目录生成的 `flash_args`：

```powershell
python -m esptool --chip esp32s3 --port COM3 --baud 921600 read_flash 0 0x1000000 <出厂备份.bin>
# 在实际构建目录中：
python -m esptool --chip esp32s3 --port COM3 --baud 921600 write_flash '@flash_args'
# 恢复时先停止占用串口的控制页，并核对备份 SHA-256：
python -m esptool --chip esp32s3 --port COM3 --baud 921600 write_flash 0 <出厂备份.bin>
```

首版曾因遗漏 XPowersLib 的 Kconfig，使旧 I²C API 与新 API 同时链接，在 `app_main` 前反复重启并黑屏。现保留上游 Kconfig、默认启用新 API，并增加编译期检查。板子仍可由 USB ROM 下载器恢复；不能把编译成功当作启动验收。

音频需要显式驱动 TCA9554 的 EXIO7（NS4150B `PA_CTRL`）。当前播放前拉高、尾音排空后拉低，并让空闲 I²S DMA 自动清零，避免残留片段重复。仅 ES8311 写入成功不能证明功放已开启。接线依据 [微雪官方原理图](https://files.waveshare.com/wiki/ESP32-S3-Touch-LCD-3.5/ESP32-S3-Touch-LCD-3.5-Schematic.pdf)。

## 协议与测试

每次请求是一行不超过 511 字节的 JSON：`id` 为正整数，`op` 为 `hello/frame/audio/record`。输出媒体请求带 `size` 与 FNV-1a 32 位 `checksum`；收到 `ready` 才上传二进制，每 4096 字节等待对应累计字节数的 `chunk` 回执，最后等待 `done` 或 `error`。图像固定 307200 字节；音频最多 960000 字节。`record` 指定 1000～10000 毫秒，板子采集 PCM16/48kHz/单声道，发送 `captured` 元数据和校验和，分块回传并等待电脑累计字节 `ack`，最后返回 `done`；长度、校验和或回执错误时丢弃，不自动重新录音。

## 本地听写

安装 `requirements-asr.txt`，使用 sherpa-onnx 1.13.8、NumPy 2.2.6 和 SenseVoice int8 CPU 模型。模型来源为发布者 [csukuangfj 的仓库](https://huggingface.co/csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17)，本次固定 revision `6a65851692da9706cbddfac66ea9b96ebb1dee21`；需要同版 `model.int8.onnx` 和 `tokens.txt`。权重 SHA-256 为 `c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51`。模型及依赖放在仓库外，不提交。

```powershell
python -m pip install -r requirements-asr.txt
python bridge.py --port COM3 --asr-model-directory <模型目录>
```

`Start-Panel.ps1` 只在运行目录 `asr/sensevoice-int8/ready.json` 存在时自动启用模型，防止加载未完成下载；该标记应在校验权重、加载和样例解码成功后建立。`/api/health` 的 `asr_ready` 表示模型已加载。`POST /api/listen` 执行一次有限录音并返回听写，不自动写 Moment，也不保存原始音频；未加载模型时不开始录音。低能量或仅标点结果返回空文字，这不是 VAD 或说话人识别。

固件录音缓冲使用 PSRAM，不能在较小的 main 任务栈中放 4KB 临时数组；USB TX ring 至少容纳单个 4096 字节分块，当前为 8192。首次实现曾分别因栈溢出复位和 TX ring 过小而失败，修正后1秒真实采集、8秒完整传输通过。首次现场听写仅得到标点，用户直接点击录音后第二次听写得到完整句子（角色名同音误字），真实主体回复的显示和声音由用户确认正常；首次无效结果的具体原因仍未确定，不能只归因于时差。

校验失败、长度超限或半途超时不执行输出。电脑验证请求编号、协议版本、回执顺序与字节数；失败后关闭连接，不自动重发媒体。超时意味着结果可能未知，协议暂不提供跨重启幂等性。序号只用于匹配本次请求。

```powershell
python -m unittest discover -s . -p 'test_*.py' -v
```

正式验收需包含：真实开机、中文画面、图片、WAV 播放，以及坏校验和/中断上传后的恢复。电脑端假串口测试只验证协议处理，不能证明硬件效果。本机备份、构建及验证记录位置放在工作区外层 `LOCAL_RUNTIME.md`。

2026-10-15：10 项电脑端测试通过，用户确认中文、红绿蓝画面及语音正常，三种异常请求后的实机恢复检查通过。可停止控制页后重跑（会替换板子画面，指定 WAV 会播放声音）：

```powershell
python hardware_check.py --port COM3 --wav <测试音频.wav> --report <仓库外回执.json>
```

远程开发时可将板子留在开发电脑的 USB 上，远程进入该电脑后打开控制页。保持开发电脑开机、不休眠。设备回执和网页预览可用于多数联调，但真实屏幕、扬声器和断电重启仍需要现场或外置摄像头验证。

2026-10-16 远程复核：真实板端握手、文字/图片、短提示音及 4 秒音频完成回执通过，三种异常上传被拒绝后可继续交互，10 项电脑测试通过。首次 WAV 等待完成回执超时；后续查询显示播放计数增加，板子未重启，短/长音频检查均完成。首次超时原因仍未确定，不自动重放未知结果。此次没有现场确认画面或实际声音，也尚未连接 WXXZ Moment 主链。
