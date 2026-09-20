# 媒体理解插件

插件提供两个能力：

- `media.source.inspect`：从分享文字中识别 B站 BV/av、分P、b23.tv，以及抖音 video/note、v.douyin.com、jx.douyin.com。它只确认来源，不声称看过内容。
- `media.video.analyze`：一次分析一个 B站或抖音视频。使用 `yt-dlp` 受限获取，`ffprobe` 校验时长，`ffmpeg` 均匀抽取最多 8 张画面，并把画面和平台提供的 VTT/SRT 字幕交给已配置的多模态模型槽。

结果明确列出画面时间点、字幕区间和覆盖限制。没有字幕时不会推测对白；当前不直接识别音轨，不能仅凭抽帧判断音乐、音效和未抽到的连续动作。原始视频、字幕和帧只存在插件临时目录，成功、失败或取消后都会清理，不逐帧写入主记忆。

## Linux Docker 依赖

容器镜像需要提供 `yt-dlp`、`ffmpeg`、`ffprobe`，并让 Host 进程可以在 `PATH` 中找到它们；也可在插件配置中填写绝对路径。多模态模型使用 Host 的 `multimodal` 显式槽，不在本插件保存模型密钥。

默认限制：单文件 150 MB、最长 1200 秒、最多 8 帧、处理超时 180 秒、最多 1 个并发分析。可在插件数据目录 `config.json` 中覆盖；范围由插件启动时校验。下载工具不读取用户级 yt-dlp 配置，临时目录使用不可预测的任务 ID，完成后递归清理。

```json
{
  "enabled": true,
  "yt_dlp_path": "yt-dlp",
  "ffmpeg_path": "ffmpeg",
  "ffprobe_path": "ffprobe",
  "max_download_mb": 150,
  "max_duration_seconds": 1200,
  "max_frames": 8,
  "process_timeout_seconds": 180,
  "max_concurrent_analyses": 1
}
```

平台可能要求登录 Cookie、地区可用性或更新版 `yt-dlp`；本插件当前没有 Cookie 配置入口。短链由 `yt-dlp` 在分析时解析，来源识别阶段不请求网络。标题和作者只作为 `page_metadata`，不会冒充视觉证据。

离线验证：

```sh
dotnet run --project Tools/MediaRealtimeCheck/MediaRealtimeCheck.csproj
```

检查器使用模拟获取器和模型验证抽帧证据、字幕解析、清理和失败边界，不访问真实平台或模型。
