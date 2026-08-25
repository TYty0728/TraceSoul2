using System.Collections.Generic;

namespace TraceSoul2.Manager
{
    /// <summary>用途槽：对话开口 / 思考 / 复盘 / 多模态 / 生图 / 语音。插件按槽或按供应商 id 解析，不必各存一份 Key。</summary>
    public static class LlmSlotNames
    {
        public const string Chat = "chat";
        public const string Thinking = "thinking";
        public const string Review = "review";
        public const string Multimodal = "multimodal";
        public const string Image = "image";
        public const string Speech = "speech";
    }

    /// <summary>一次解析结果（含密钥，仅进程内给插件用，不进公开 API）。</summary>
    public sealed class LlmEndpointData
    {
        public string ProviderId { get; set; }
        public string Type { get; set; }
        public string DisplayName { get; set; }
        public string BaseUrl { get; set; }
        public string ApiKey { get; set; }
        public string Model { get; set; }
        public int TimeoutSeconds { get; set; }
        public string Proxy { get; set; }
    }

    public sealed class LlmModelBriefData
    {
        public string Id { get; set; }
        public bool Enabled { get; set; }
        public List<string> Roles { get; set; }
    }

    public sealed class LlmProviderBriefData
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Type { get; set; }
        public string BaseUrl { get; set; }
        public bool HasApiKey { get; set; }
        public List<LlmModelBriefData> Models { get; set; }
    }

    /// <summary>宿主注入：按供应商或用途槽解析基址与密钥。</summary>
    public interface ILlmProviderDirectory
    {
        LlmEndpointData Resolve(string providerId, string model = null);
        LlmEndpointData ResolveSlot(string slot);
        IReadOnlyList<LlmProviderBriefData> ListBrief();
        /// <summary>按供应商和模型创建隔离客户端；插件可借此使用专用模型而不替换主对话模型。</summary>
        ILlmClient CreateClient(string providerId, string model = null, bool? thinkingOverride = null);
        /// <summary>复盘槽客户端（关闭思考）。槽未指定时用对话开口关思考。无 Key 返回 null。</summary>
        ILlmClient CreateReviewClient();
    }
}
