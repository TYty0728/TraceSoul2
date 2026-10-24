using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TraceSoul2.Logic
{
    /// <summary>A provider explicitly declined the content; never includes the rejected material.</summary>
    public sealed class LlmContentRejectedException : InvalidOperationException
    {
        public LlmContentRejectedException() : base("模型因内容审核拒绝本条加工。") { }
    }

    public static class LlmContentRejectionLogic
    {
        public static void CheckResponse(string body)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(body ?? "{}"); }
            catch (JsonException) { return; }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                    (BlockedCode(Text(error, "code")) || BlockedCode(Text(error, "type")) ||
                     Regex.IsMatch(Text(error, "message"),
                         @"content[_ ](?:filter|policy)|sensitive_words_detected|data.?inspection.?failed|内容审核(?:未通过|不通过|失败)|(?:请求|输入|输出|生成的?)内容.{0,20}(?:不安全|敏感|违反.{0,10}(?:政策|规定))",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                    throw new LlmContentRejectedException();
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (BlockedCode(Text(choice, "finish_reason"))) throw new LlmContentRejectedException();
                    if (choice.ValueKind == JsonValueKind.Object && choice.TryGetProperty("message", out var message) &&
                        !string.IsNullOrWhiteSpace(Text(message, "refusal"))) throw new LlmContentRejectedException();
                }
                if (root.TryGetProperty("promptFeedback", out var feedback) && BlockedCode(Text(feedback, "blockReason")))
                    throw new LlmContentRejectedException();
                if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0 &&
                    BlockedCode(Text(candidates[0], "finishReason"))) throw new LlmContentRejectedException();
            }
        }

        private static string Text(JsonElement value, string name)
            => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
                ? field.GetString() ?? "" : "";

        private static bool BlockedCode(string code)
            => (code ?? "").Trim().ToLowerInvariant() is "content_filter" or "content_policy_violation" or
                "sensitive_words_detected" or "inappropriate_content" or "content_blocked" or
                "datainspectionfailed" or "inputdatainspectionfailed" or "outputdatainspectionfailed" or
                "data_inspection_failed" or "safety" or "blocklist" or "prohibited_content" or "spii";

        // Match an explicit refusal of this writing task, not a sensitive word inside a remembered event.
        public static bool IsRefusal(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || text.Length > 1200) return false;
            var chinese = Regex.IsMatch(text,
                @"^(?:(?:很|非常)?抱歉[，,。!！\s]*|对不起[，,。!！\s]*)?(?:我|本模型|作为.{0,15}(?:模型|助手)).{0,12}(?:无法|不能|不便|不被允许)(?:为你|为您|帮助你|帮你|协助你|协助您)?.{0,8}(?:提供|生成|描述|处理|协助|完成|满足|回应)");
            var english = Regex.IsMatch(text,
                @"^(?:(?:I(?:'m| am) )?sorry[,!.\s]*(?:but\s+)?)?I\s+(?:cannot|can't|am unable to|must decline to)\s+(?:help|assist|provide|generate|fulfill|comply|process)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return (chinese || english) && Regex.IsMatch(text,
                @"(?:这个|该|你的|您的|此项|这项)请求|(?:这类|此类|这段|该)内容|(?:这个|该)任务|内容(?:政策|限制|审核)|安全(?:政策|准则)|敏感内容|违规内容|不当内容|色情内容|policy|guidelines|(?:this|that|your) request|explicit (?:sexual )?content",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public static void CheckRefusal(string text)
        {
            if (IsRefusal(text)) throw new LlmContentRejectedException();
        }
    }
}
