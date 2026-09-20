using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins
{
    public sealed partial class QqQzonePlugin
    {
        private async Task<TraceCapabilityResultData> PrepareDraftAsync(
            BrainCapabilityCallData call, TraceTurnContext context, CancellationToken cancellationToken)
        {
            var content = call.GetArgument("content").Trim();
            if (content.Length == 0) content = call.GetArgument("text").Trim();
            var allowImage = call.GetArgument("allow_image") == "true";
            var imagePrompt = (call.GetArgument("image_prompt") ?? "").Trim();
            if (content.Length == 0 && IsIdleCall(call))
            {
                var seed = call.GetArgument("seed") ?? "";
                seed += allowImage ? "\n相机当前可用，可以自行决定是否配图。" : "\n相机当前不可用，image_prompt 必须留空。";
                var draft = ParseDraft(await ComposePublishAsync(seed, context, cancellationToken));
                content = draft.Content;
                imagePrompt = allowImage ? draft.ImagePrompt : "";
            }
            else if (content.Length > 0 && allowImage && call.GetArgument("image_prompt", null) == null && context?.Services?.Llm != null)
            {
                var llm = context.Services.Llm;
                var packer = context.Services.ContextPack;
                var user = "已写好的说说：\n" + content + "\n本轮请求：\n" + (context.Moment?.Content ?? "");
                var messages = packer != null
                    ? packer.Assemble(llm, context, context.Workspace?.SharedMemory ?? "", user,
                        "【说说配图意图】", QqQzonePrompts.ExistingPostImageInstructions)
                    : new List<DeepSeekMessageData>
                    {
                        new DeepSeekMessageData("system", QqQzonePrompts.ExistingPostImageInstructions),
                        new DeepSeekMessageData("user", user)
                    };
                imagePrompt = ParseDraft(await llm.CompleteTextAsync(messages, cancellationToken,
                    packer?.BuildPromptCacheKey(llm, context.ConversationId))).ImagePrompt;
            }
            if (LooksLikeNone(content))
                return new TraceCapabilityResultData { Status = "skipped", Summary = "没有想发布的说说。" };
            if (LooksLikeNone(imagePrompt)) imagePrompt = "";
            return new TraceCapabilityResultData
            {
                Status = "success", Summary = imagePrompt.Length == 0 ? "说说文字已准备。" : "说说及配图意图已准备。",
                Payload = JsonSerializer.Serialize(new { content, image_prompt = imagePrompt })
            };
        }

        internal static (string Content, string ImagePrompt) ParseDraft(string raw)
        {
            var text = StripCompose(raw);
            if (LooksLikeNone(text)) return ("", "");
            if (!text.StartsWith("{", StringComparison.Ordinal)) return (text, "");
            using var doc = JsonDocument.Parse(text);
            return (ReadString(doc.RootElement, "content") ?? "", ReadString(doc.RootElement, "image_prompt") ?? "");
        }
    }
}
