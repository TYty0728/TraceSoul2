using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.Logic
{
    // 中枢编排器官：空间决定正文/分享意图，相机负责画面，空间将图文一次发布。
    public static class QzonePublishLogic
    {
        public static async Task<TraceCapabilityResultData> ExecuteAsync(
            BrainCapabilityCallData source, TraceTurnContext turn,
            IEnumerable<TraceContributionDescriptorData> catalog,
            Func<BrainCapabilityCallData, CancellationToken, Task<TraceCapabilityResultData>> execute,
            CancellationToken cancellationToken)
        {
            var items = (catalog ?? Enumerable.Empty<TraceContributionDescriptorData>()).Where(x => x != null).ToList();
            var publisher = items.FirstOrDefault(x => x.Id == source.capability_id);
            // 旧版空间插件不知道 prepare，会直接发布；绝不能把准备请求交给旧插件。
            if (publisher == null || !(publisher.ParametersJsonSchema ?? "").Contains("image_prompt"))
                return await execute(source, cancellationToken);
            var camera = items.FirstOrDefault(x => x.Id == "qq.imagegen.generate");
            var prepared = await execute(Copy(source,
                ("dispatch", "prepare"), ("allow_image", camera == null ? "false" : "true")), cancellationToken);
            if (prepared == null || prepared.Status != "success") return prepared;
            using var json = JsonDocument.Parse(prepared.Payload);
            var content = json.RootElement.GetProperty("content").GetString() ?? "";
            var imagePrompt = json.RootElement.GetProperty("image_prompt").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(content))
                return new TraceCapabilityResultData { Status = "skipped", Summary = "没有想发布的说说。" };
            if (string.IsNullOrWhiteSpace(imagePrompt))
                return await execute(Copy(source, ("dispatch", "publish"), ("content", content)), cancellationToken);
            if (camera == null)
                return new TraceCapabilityResultData { Status = "failed", Summary = "想配图，但相机当前不可用；本次说说未发布。" };

            string files = null;
            try
            {
                var generated = await execute(new BrainCapabilityCallData
                {
                    call_id = source.call_id + "-image", capability_id = camera.Id,
                    purpose = "为同一条空间说说准备配图",
                    arguments = new List<BrainCallArgumentData>
                    {
                        new BrainCallArgumentData { name = "dispatch", value = "generate" },
                        new BrainCallArgumentData { name = "prompt", value = imagePrompt }
                    }
                }, cancellationToken);
                files = generated?.Payload;
                if (generated?.Status != "success" || string.IsNullOrWhiteSpace(files))
                    return new TraceCapabilityResultData { Status = "failed", Summary = "配图未生成成功，本次说说未发布。" };
                // 发布动作只有这一次；不先发文字、不额外私聊发图、不在失败后自动重发。
                return await execute(Copy(source, ("dispatch", "publish"), ("content", content),
                    ("files", files)), cancellationToken);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(files) && (camera.ParametersJsonSchema ?? "").Contains("release"))
                {
                    try
                    {
                        await execute(new BrainCapabilityCallData
                        {
                            call_id = source.call_id + "-release", capability_id = camera.Id,
                            arguments = new List<BrainCallArgumentData>
                            {
                                new BrainCallArgumentData { name = "dispatch", value = "release" },
                                new BrainCallArgumentData { name = "files", value = files }
                            }
                        }, CancellationToken.None);
                    }
                    catch { turn?.Services?.LogTiming(turn.TraceId, "空间配图临时文件清理未完成"); }
                }
            }
        }

        private static BrainCapabilityCallData Copy(BrainCapabilityCallData source, params (string Name, string Value)[] values)
        {
            var args = (source.arguments ?? new List<BrainCallArgumentData>())
                .Where(x => x != null && !string.Equals(x.name, "dispatch", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(x.name, "files", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(x.name, "allow_image", StringComparison.OrdinalIgnoreCase) &&
                            !values.Any(v => string.Equals(v.Name, x.name, StringComparison.OrdinalIgnoreCase)))
                .Select(x => new BrainCallArgumentData { name = x.name, value = x.value }).ToList();
            args.AddRange(values.Select(x => new BrainCallArgumentData { name = x.Name, value = x.Value }));
            return new BrainCapabilityCallData
            {
                call_id = source.call_id, capability_id = source.capability_id,
                purpose = source.purpose, arguments = args
            };
        }
    }
}
