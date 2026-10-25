# 本地千问语音

2026-10-19：增加固定唤醒应答缓存模块`wake_cache.py`（安装清单已包含）。原语音接口收到`X-TraceSoul-Wake-Cache: v1`时才启用，普通请求不缓存；返回`hit/miss`头，命中仍校验Bearer，不提供公共文件地址。文字、参数、模型和实际角色参考音改变会失效，私有缓存最多16份，详见[协议与验证](../../docs/WXXZ_WAKE_AUDIO_CACHE.md)。

独立的 Qwen3-TTS 0.6B Base 音色克隆服务，面向 Windows + NVIDIA GPU。只负责把文字读成语音，不启动 TraceSoul2 Host、不连接 QQ、不自动录音，也不改现有云端供应商。合成时只使用本地模型，没有按字符或按时长的云端 TTS 调用费用；现有心智模型若仍走 API，其费用和延迟仍然存在。

模型由本服务的 Python/PyTorch 从运行目录的 `models/Qwen3-TTS-12Hz-0.6B-Base` 直接加载，不经过 Ollama，也不使用 Ollama 的模型仓库。清理 Ollama 模型不影响这套独立保存的 TTS 权重；角色参考音频与音色选择保存在同一运行目录的 `voices/`。

模型：[Qwen 官方仓库](https://github.com/QwenLM/Qwen3-TTS)，权重固定到 `5d83992436eae1d760afd27aff78a71d676296fc`。Python 3.12、PyTorch/Torchaudio 2.8.0 CUDA 12.8、qwen-tts 0.1.1；不用 Windows 上额外编译的 FlashAttention，采用 PyTorch SDPA。第一次安装下载约6GB的权重和GPU依赖，解压及缓存会占用更多空间。

## 安装、启动与试听

在本目录运行，`RuntimeDirectory` 选择源码库以外的持久目录：

```powershell
./Install-LocalVoice.ps1 -RuntimeDirectory <本地语音目录>
./Start-LocalVoice.ps1 -RuntimeDirectory <本地语音目录>
./Stop-LocalVoice.ps1 -RuntimeDirectory <本地语音目录>
```

安装器用现有 `python` 引导 uv，再创建独立环境，不替换系统 Python/PyTorch。启动器隐藏运行，仅监听 `127.0.0.1:5088`，不设置开机启动。打开 `http://127.0.0.1:5088/` 试听；首次启动加载模型后才开放服务。退出时用停止脚本释放GPU显存。

Windows系统PowerShell禁止执行脚本时，可直接用已安装的Python运行本目录 `launch.py --runtime <本地语音目录> --open`，无需更改执行策略。它在后台启动服务、等待模型ready后打开试听页；已就绪则复用，启动锁避免重复双击同时加载模型。仅打开网页不会启动TTS；选择本地千问的板端播音需要5088保持运行，关闭试听标签不会停止后台服务。

## 私有接口鉴权

首次启动在运行目录生成独立随机密钥 `access.json`，重启复用，损坏时拒绝启动而不悄悄更换。凭据文件仅当前用户和SYSTEM可访问（Windows），Unix权限600；不打印或进入仓库。每个 `/v1/` 请求都必须携带 `Authorization: Bearer <API Key>`，缺少/错误返回401，在参数解析及模型调用前拒绝；URL查询参数不接受密钥。

网页不保存长期Key。直接打开5088显示验证页；启动器或已登录TraceSoul2的工作台入口在服务端用Key换取60秒内一次性登录票据，再建立8小时HttpOnly/SameSite会话。票据不是长期API Key，不能作为API凭据；网页试读使用独立的 `/ui/speech`，`/v1/` 即使有浏览器会话仍要求Bearer。音色列表、候选音频、克隆/选择接口均受保护。未验证的health只提供最小ready状态，不返回音色与耗时信息。过期后从启动器或Host入口重新打开即可。

将Host与服务配对（只输出成功状态，不打印Key）：

```powershell
<运行目录>/venv/Scripts/python.exe Tools/LocalVoice/configure_access.py --runtime <运行目录> --client-config <Host插件数据目录>/local-voice/config.json
```

客户端私密文件包含完整 `api_url` 与 `api_key`，Host自动读取，不放入角色普通音色设置或公开状态。已有不同配置拒绝覆盖；此命令只配对本机，迁移服务器需另行安全传递凭据。客户端允许HTTPS或本机回环HTTP，拒绝URL中的用户信息、查询参数和非回环明文HTTP；仅有API Key不能防传输窃听，跨网络应使用HTTPS或加密隧道。参考[OWASP REST安全指南](https://cheatsheetseries.owasp.org/cheatsheets/REST_Security_Cheat_Sheet.html#https)。本次不开放公网、不部署NewAPI，也不更改Windows脚本策略。

Host供应商版首次启动会将上述配对配置导入「大脑 · LLM → 本地千问 TTS」，只在语音槽为空时选中；此后地址、Key、模型及音色统一在该供应商修改，不再从旧文件重复覆盖。供应商协议 `qwen_tts` 只能用于语音槽，不能设为聊天或JEV。模型行「用于语音」选择该供应商；面板「输入文字，听ta说」生成网页试听WAV，「音色抽卡」通过一次性票据打开工作台。保存供应商、取模型或试听均不启动通话、不发Moment、不播放板子；开发板仍需选择本地千问输出方式。迁移与验证见[角色语音](../../docs/WXXZ_SPEECH.md)。

服务常驻模型，按参考文件修改时间缓存最多四个音色的提示特征。当前接口返回**整段 WAV**，健康状态明确 `streaming=false`；不是97ms首包或已经接通的实时电话。先用短句实测等待时间，再做流式音频输出。其他GPU任务可能影响显存与速度。

## 推理加速与耗时

默认对固定0.6B模型的16码本预测器使用CUDA计算图，预热在启动完成之前进行，不改变权重、参考或采样规则。加速路径限定单条输入、15个预测步、temperature=0.8/top_p=0.95/top_k=50；其它参数/形状继续走原有预测器，同一音色不变。模型的其它生成步骤仍使用原实现，不替换整套推理框架。

通过鉴权的 `GET /health` 返回 `acceleration=predictor_cuda_graph`（启用）或 `standard`，并提供最后一次成功合成的纯耗时统计。`reference_ms/tokens_ms/decode_ms/wav_ms` 分别为参考处理、语音码生成、解码和WAV编码，接口也返回对应的 `X-Reference-Ms/X-Tokens-Ms/X-Decode-Ms/X-Wav-Ms`；没有原文字段或声纹向量。

如需禁用，先停止服务，再用启动脚本的 `-NoPredictorGraph`，或直接给server.py传 `--no-predictor-graph`。启动脚本不修改已在运行的服务。

2026-10-18同机同音色、同文字和种子的预热后对照：0.96秒音频1527→649ms，5.20秒音频7660→3086ms，3.36秒音频4942→2028ms。三份WAV逐字节一致；另0.7/0.9参数的普通路径也一致（2034→2039ms）。这是四组实际对照，不能保证所有未来输入/环境逐字节复现。网页实际默认参数生成5.60秒音频，等待3.73秒；仍为整段生成，流式播放尚未接入。

## 输入文字与音色抽卡

网页顶部选择试听音色，输入文字，点「生成试听」，完成后点击播放器。可选择当前日常角色参考、原始角色参考或已生成的候选；试读不更改日常设置。

下方「本地音色抽卡」保存候选音频，各卡可直接播放。点「用这张读上方文字」后，可输入自己的句子测试这份候选。确认喜欢后再点「设为日常音色」，使 `character` 使用该候选作为克隆参考；后续说话仍会有自然变化。展开生成参数可连续生成2～4张，种子依次递增，调整变化幅度/采样范围。每张完成即保留，可在当前张完成后停止。这里调整的是采样和参考音频，**没有训练模型权重**。

候选存在运行目录 `lab/cards/`，原始合成角色参考存在 `voices/source_remote/`；选择通过 `voices/character/active.json` 原子切换，不覆盖原始WAV和其他候选。恢复按钮回到原始角色参考。候选作为参考需3～30秒；太短的候选仍可播放，但不能用于克隆。抽卡与通话共用同一GPU模型，生成前应结束通话。

当前采用采样生成，默认种子0、temperature=0.8、top_p=0.95；支持设置种子以便比较，不保证跨环境逐字节复现。底层生成设90秒停止时限，超时结果不作为成功音频保存；参考特征提取和解码另需时间，并非整个请求严格90秒。`/health` 提供当前阶段和实际已用时间。

## 放入角色音色

在运行目录放入：

```text
voices/
  character/
    reference.wav
    reference.txt
```

参考音频建议10～20秒，允许3～30秒，单人清晰说话、无配乐；`reference.txt` 用UTF-8填写准确对白。刷新页面选择“角色音色”即可，下一次合成自动更新特征。支持增加其他合法英文/数字/下划线名称的音色子目录，结构相同。

`demo` 来自官方公开示例，只用于部署验收，不是用户角色音色；未配置 `character` 时请求它会明确失败，不会偷偷回退到示例声音。用户参考文件、模型、试听产物只留在独立运行目录，不进入仓库。

## 本地接口

通过鉴权的 `GET /health` 返回模型/GPU就绪状态、音色是否具备参考文件；带Bearer的 `GET /v1/models` 返回 `qwen3-tts-local`。

`POST /v1/audio/speech` 支持：

```json
{"model":"qwen3-tts-local","input":"我在呢。","voice":"character","response_format":"wav"}
```

最多240字，建议电话短句；可选 `language` 为 `Chinese`（默认）、`English`、`Auto`。响应为PCM16 WAV，并提供 `X-Synthesis-Ms`、`X-Audio-Ms`、`X-RTF`（合成耗时/音频时长）用于测量。只接受一个并发合成，忙时返回429。当前不支持MP3、云端emotion字段或任意外部参考URL。接口已要求独立Bearer鉴权，监听仍仅回环；加入Key不等于已部署远端HTTPS。

抽卡接口：`GET /lab/cards`、`POST /lab/cards`（text/voice/seed/temperature/top_p）、`GET /lab/cards/{id}/audio`。任意文字试读为 `POST /lab/cards/{id}/preview`（text），仅返回WAV，不改变日常音色。`POST /lab/activate`（id）选择参考，`POST /lab/restore` 恢复原始参考；原始音频对照为 `GET /lab/original/audio`。最多保存200张、列表展示最近100张，无自动删除。

后续接现有 QQ TTS 可选择本地接口、`model=qwen3-tts-local`、`voice_id=character`、`prompt_mode=raw`、`audio_format=wav`、`max_text_length<=240`。供应商Key必须填本服务的独立API Key，不能用占位值或云端Key。**本轮未切换现有供应商或拨打真人电话。** 远端Docker里的 `127.0.0.1` 不指向这台Windows电脑，需要另行设计受保护的连接。

## 验证

```powershell
<运行目录>/venv/Scripts/python.exe -m unittest discover -s Tools/LocalVoice -p test_server.py
node scripts/tests/test-local-voice-ui.cjs
<运行目录>/venv/Scripts/python.exe Tools/LocalVoice/benchmark.py --runtime <运行目录> --output <运行目录>/samples
<运行目录>/venv/Scripts/python.exe Tools/LocalVoice/profile_inference.py --runtime <运行目录> --output <运行目录>/samples/profile --predictor-graph
<运行目录>/venv/Scripts/python.exe Tools/LocalVoice/check_acceleration.py --runtime <运行目录> --output <运行目录>/samples/parity
```

接口回归使用假模型，验证缺失音色、参数、回环来源、参考目录约束及WAV/计时响应。实际模型验收另生成三段官方示例音色的中文试听，记录冷/热请求耗时与音频时长；本机结果放在运行目录的 `samples/benchmark.json` 和工作区外层 `LOCAL_RUNTIME.md`。不据离线接口回归声称模型速度、音色相似度或真实通话已验收。

2026-10-18私密接口已本机载入：18项Python、两页专项检查及Host/WXXZ专项通过。真实服务无Key/错误Key/查询参数Key均401，正确Key查询成功；单次票据只能兑换一次，网页会话不能替代API Bearer。实际Host同源客户端携带独立Key完成角色WAV合成，并成功换取和兑换工作台票据；未做浏览器点击或板端播放验收，没有开放远端访问。

性能分析和真实模型一致性检查需要先停止5088服务，避免重复加载模型造成显存竞争；检查只使用固定公开测试文字，不采音/调用云端/发Moment。分析工具可选 `--worker` 比较工作线程，`--profiler` 保存算子计时。对照检查使用当前character参考（不会改日常音色），输出基线/加速音频、SHA256和耗时，不包含原私人对白。

2026-10-17角色参考实测：采样短句1.60秒音频合成8.435秒；两张同文候选分别5.20秒/31.012秒、5.44秒/19.547秒（音频/合成）。本地ASR复核两张均包含完整测试句，但这不能证明听感更自然。此前贪心生成曾等待180秒客户端超时。当前推理仍慢，尚未解决流式首包或持续通话延迟；不能因本地GPU ready就声称实时。

本模块只负责TTS；WXXZ本地ASR、本人声纹及半双工输入链见[语音通话](../../docs/WXXZ_VOICE_CALL.md)，全双工回声消除与QQ音频设备网关仍未包含。
