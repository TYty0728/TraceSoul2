using System.Collections.Generic;

namespace TraceSoul2.Manager
{
    /// <summary>用途槽：主模型（思考与表达） / JEV决策 / 复盘 / 识图多模态 / 生图 / 语音。插件按槽或供应商解析。</summary>
    public static class LlmSlotNames
    {
        public const string Chat = "chat";
        public const string Thinking = "thinking"; // Legacy alias of Chat; no independent selection.
        public const string Jev = "jev";
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
        /// <summary>解析用途槽；JEV只使用明确配置，不按角色标记自动匹配。</summary>
        LlmEndpointData ResolveSlot(string slot);
        /// <summary>只返回控制台该槽明确指定的模型。未指定返回 null，不按角色自动匹配。</summary>
        LlmEndpointData ResolveExplicitSlot(string slot);
        IReadOnlyList<LlmProviderBriefData> ListBrief();
        /// <summary>按供应商和模型创建隔离客户端；插件可借此使用专用模型而不替换主对话模型。</summary>
        ILlmClient CreateClient(string providerId, string model = null, bool? thinkingOverride = null);
        /// <summary>复盘槽客户端（关闭思考）。槽未指定时用主模型关思考。无 Key 返回 null。</summary>
        ILlmClient CreateReviewClient();
    }
}
