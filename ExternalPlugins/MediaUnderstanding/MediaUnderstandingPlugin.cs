using System.Diagnostics;
using System.Text.Json;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins.MediaUnderstanding;

public sealed class MediaUnderstandingPlugin : ITracePlugin
{
    private SemaphoreSlim? analysisGate;
    private MediaVideoPipeline? pipeline;
    private MediaUnderstandingConfig? config;

    public TracePluginMetadataData Metadata { get; } = new()
    {
        Id = "media.understanding", DisplayName = "媒体理解", Version = "0.2.0",
        Author = "TraceSoul2", Role = PluginRoleValues.Organ, PlatformId = "",
        Description = "统一识别 B站/抖音分享链接，受限下载视频、抽取时间点画面与可用字幕并调用多模态模型分析。"
    };

    public void Register(TracePluginContext context)
    {
        var dataDirectory = context.PluginDataDirectory ??
                            Path.Combine(context.Services.DataDirectory ?? string.Empty, "media-understanding");
        Directory.CreateDirectory(dataDirectory);
        config = MediaUnderstandingConfig.Load(context.PackageDirectory, dataDirectory);
        analysisGate = new SemaphoreSlim(config.MaxConcurrentAnalyses, config.MaxConcurrentAnalyses);
        var runner = new ExternalCommandRunner();
        pipeline = new MediaVideoPipeline(
            new YtDlpMediaAcquirer(config, runner, dataDirectory),
            new FfmpegVideoFrameExtractor(config, runner),
            new MultimodalFrameAnalyzer());
        context.AddCallable(new InspectSource());
        context.AddCallable(new AnalyzeVideo(config, analysisGate, pipeline, context.Services));

        var dependencies = new[]
        {
            ("yt-dlp", config.YtDlpPath), ("ffmpeg", config.FfmpegPath), ("ffprobe", config.FfprobePath)
        };
        var missing = dependencies.Where(x => !ExternalCommandRunner.CanResolve(x.Item2)).Select(x => x.Item1).ToArray();
        Metadata.Note = !config.Enabled ? "已由配置停用视频分析；仍可识别来源。"
            : missing.Length > 0 ? "视频分析缺少依赖：" + string.Join("、", missing) + "。"
            : "视频抽帧与平台字幕分析已就绪；尚未直接识别音轨。";
        context.Services.LogTiming(null, "媒体理解插件已加载", detail:
            "version=" + Metadata.Version + "｜enabled=" + config.Enabled + "｜missing=" +
            (missing.Length == 0 ? "none" : string.Join(",", missing)));
    }

    public void Shutdown()
    {
        pipeline = null;
        config = null;
        // 正在执行的调用由宿主生命周期取消；不在卸载时 Dispose 信号量，避免竞态。
        analysisGate = null;
    }

    private sealed class InspectSource : ITraceCallableContribution
    {
        public TraceContributionDescriptorData Descriptor { get; } = new()
        {
            Id = "media.source.inspect", Kind = TraceContributionKindValues.CallableNerve,
            DisplayName = "识别媒体来源", Provides = "media.source",
            Description = "识别 B站/抖音分享链接与分P，只返回来源，不把页面信息说成视频内容。",
            WhenToUse = "用户分享 B站或抖音链接，需要确认来源或决定是否进一步分析时。",
            WhenNotToUse = "不能用来源识别结果回答视频情节、画面或声音问题。",
            ParametersJsonSchema = "{text:string}", HasExternalSideEffect = false
        };

        public bool IsAvailable(TraceTurnContext context) => context != null;

        public Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call,
            TraceTurnContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sources = MediaSourceParser.Parse(call.GetArgument("text"));
            var results = sources.Select(source => new MediaUnderstandingResultData
            {
                Source = source, Status = "not_analyzed", Summary = "已识别来源，尚未获取或分析内容。",
                Limitations = new List<string> { "只有来源信息，没有画面、字幕或音轨证据。" }
            });
            return Task.FromResult(new TraceCapabilityResultData
            {
                Status = sources.Count > 0 ? "success" : "unavailable",
                Summary = sources.Count > 0 ? "已识别媒体来源；尚未看过内容。" : "没有识别到支持的 B站或抖音链接。",
                Payload = JsonSerializer.Serialize(results)
            });
        }
    }

    private sealed class AnalyzeVideo : ITraceCallableContribution
    {
        private readonly MediaUnderstandingConfig config;
        private readonly SemaphoreSlim gate;
        private readonly MediaVideoPipeline pipeline;
        private readonly TracePluginServices services;

        public AnalyzeVideo(MediaUnderstandingConfig config, SemaphoreSlim gate,
            MediaVideoPipeline pipeline, TracePluginServices services)
        {
            this.config = config;
            this.gate = gate;
            this.pipeline = pipeline;
            this.services = services;
        }

        public TraceContributionDescriptorData Descriptor { get; } = new()
        {
            Id = "media.video.analyze", Kind = TraceContributionKindValues.CallableNerve,
            DisplayName = "分析分享视频", Provides = "media.video.visual_understanding",
            Description = "受限获取一个 B站或抖音视频，均匀抽取带时间戳的画面和平台字幕，并用多模态模型回答问题。",
            WhenToUse = "用户要求看、概括或询问一个 B站/抖音分享视频的画面内容时。",
            WhenNotToUse = "当前不用于抖音图文；没有字幕时不能回答对白，也不能判断音效、音乐或未抽到的连续动作。",
            ParametersJsonSchema = "{url?:string,text?:string,question?:string}",
            OutputJsonSchema = "{status,source,summary,evidence[],limitations[]}",
            HasInternalMutation = true, HasExternalSideEffect = false
        };

        public bool IsAvailable(TraceTurnContext context)
        {
            if (!config.Enabled || context?.Services?.Providers == null) return false;
            if (!ExternalCommandRunner.CanResolve(config.YtDlpPath) ||
                !ExternalCommandRunner.CanResolve(config.FfmpegPath) ||
                !ExternalCommandRunner.CanResolve(config.FfprobePath)) return false;
            var endpoint = context.Services.Providers.ResolveExplicitSlot(LlmSlotNames.Multimodal);
            return endpoint != null && !string.IsNullOrWhiteSpace(endpoint.ApiKey);
        }

        public async Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call,
            TraceTurnContext context, CancellationToken cancellationToken)
        {
            var input = string.Join(" ", new[] { call.GetArgument("url"), call.GetArgument("text") }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            var sources = MediaSourceParser.Parse(input);
            if (sources.Count != 1)
                return Failure(sources.FirstOrDefault(), sources.Count == 0 ? "unsupported_source" : "multiple_sources");
            var source = sources[0];
            if (source.Kind != "video") return Failure(source, "unsupported_media_kind");

            var entered = false;
            var timer = Stopwatch.StartNew();
            try
            {
                await gate.WaitAsync(cancellationToken);
                entered = true;
                var result = await pipeline.AnalyzeAsync(source, call.GetArgument("question"), context, cancellationToken);
                services.LogTiming(context.TraceId, "视频画面分析完成", timer.ElapsedMilliseconds,
                    "platform=" + source.Platform + "｜frames=" + result.Evidence.Count(x => x.Kind == MediaEvidenceKinds.VideoFrame));
                return new TraceCapabilityResultData
                {
                    Status = "success", Summary = result.Summary, Payload = JsonSerializer.Serialize(result),
                    EvidenceRefs = result.Evidence.Select(x => x.EvidenceId).Where(x => x.Length > 0).ToList()
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (MediaPipelineException error)
            {
                services.LogTiming(context.TraceId, "视频画面分析未完成", timer.ElapsedMilliseconds,
                    "platform=" + source.Platform + "｜code=" + error.Code);
                return Failure(source, error.Code);
            }
            catch
            {
                services.LogTiming(context.TraceId, "视频画面分析未完成", timer.ElapsedMilliseconds,
                    "platform=" + source.Platform + "｜code=internal_error");
                return Failure(source, "internal_error");
            }
            finally
            {
                if (entered) gate.Release();
            }
        }

        private static TraceCapabilityResultData Failure(MediaSourceData? source, string code)
        {
            var message = code switch
            {
                "unsupported_source" => "没有识别到支持的 B站或抖音视频链接。",
                "multiple_sources" => "一次只能分析一个视频链接。",
                "unsupported_media_kind" or "image_post_not_implemented" => "当前视频分析入口暂不支持图文作品。",
                "yt_dlp_unavailable" or "ffmpeg_unavailable" or "dependency_unavailable" => "媒体处理依赖尚未安装或路径不可用。",
                "multimodal_unconfigured" => "多模态识图模型尚未配置。",
                "video_too_large" => "视频超过配置的下载大小上限。",
                "video_too_long" => "视频超过配置的时长上限。",
                "process_timeout" => "媒体获取或处理超时。",
                "model_failed" or "model_empty" => "画面模型本次没有完成分析。",
                "no_frames" or "invalid_video" or "invalid_duration" => "没有从该视频取得足够的有效画面。",
                _ => "无法安全获取或分析这个视频。"
            };
            var result = new MediaUnderstandingResultData
            {
                Source = source ?? new MediaSourceData(), Status = "failed", Summary = message,
                Limitations = new List<string> { "failure_code=" + code, "本次没有形成视频内容证据，不能据此回答视频细节。" }
            };
            return new TraceCapabilityResultData
            {
                Status = code is "unsupported_source" or "multiple_sources" or "unsupported_media_kind" ? "unavailable" : "failed",
                Summary = message, Payload = JsonSerializer.Serialize(result)
            };
        }
    }
}
