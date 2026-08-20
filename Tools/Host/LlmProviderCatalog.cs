using System;
using System.Collections.Generic;

namespace TraceSoul2.Host
{
    /// <summary>
    /// 供应商模板，字段与 AstrBot <c>provider_group.metadata.provider.config_template</c> 对齐。
    /// 只抄对话类（chat_completion），不抄 TTS / 嵌入 / 重排序。
    /// </summary>
    public static class LlmProviderCatalog
    {
        public const string OpenAiChat = "openai_chat_completion";
        public const string GoogleGenAi = "googlegenai_chat_completion";

        public static IReadOnlyList<LlmProviderTemplate> Templates()
        {
            return new[]
            {
                T("OpenAI Compatible", "openai", OpenAiChat, "https://api.openai.com/v1", "gpt-4o", 0.6f),
                T("DeepSeek", "deepseek", OpenAiChat, "https://api.deepseek.com/v1", "deepseek-v4-flash", 0.3f),
                T("Google Gemini", "google_gemini", GoogleGenAi, "https://generativelanguage.googleapis.com/", "gemini-2.5-flash", 0.7f),
                T("Gemini_OpenAI_API", "google_gemini_openai", OpenAiChat,
                    "https://generativelanguage.googleapis.com/v1beta/openai/", "gemini-2.5-flash", 0.7f),
                T("AIHubMix", "aihubmix", OpenAiChat, "https://aihubmix.com/v1", "", 0.7f),
                T("OpenRouter", "openrouter", OpenAiChat, "https://openrouter.ai/api/v1", "", 0.7f),
                T("Moonshot", "moonshot", OpenAiChat, "https://api.moonshot.cn/v1", "", 0.6f),
                T("Ollama", "ollama", OpenAiChat, "http://127.0.0.1:11434/v1", "", 0.7f)
            };
        }

        public static LlmProviderTemplate Find(string key)
        {
            foreach (var item in Templates())
            {
                if (string.Equals(item.key, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.id, key, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
            return null;
        }

        public static string NormalizeType(string type)
        {
            type = (type ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(type) ||
                string.Equals(type, "openai_compatible", StringComparison.OrdinalIgnoreCase))
                return OpenAiChat;
            return type;
        }

        public static bool IsGeminiNative(string type)
        {
            return string.Equals(NormalizeType(type), GoogleGenAi, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>按模型 id 猜用途：对话 / 思考 / 多模态 / 生图 / 语音。嵌入与重排不标对话。</summary>
        public static List<string> GuessRoles(string modelId)
        {
            var id = (modelId ?? string.Empty).Trim().ToLowerInvariant();
            var roles = new List<string>();
            if (id.Length == 0) return roles;
            if (ContainsAny(id, "embed", "bge-", "text-embedding", "rerank", "bge-reranker"))
                return roles;
            if (ContainsAny(id, "tts", "whisper", "speech", "audio", "voice-", "sovits", "cosyvoice"))
                roles.Add("speech");
            if (ContainsAny(id, "dall-e", "dalle", "gpt-image", "imagen", "flux", "kolors",
                    "stable-diffusion", "sdxl", "midjourney", "image-gen", "seedream"))
                roles.Add("image");
            if (ContainsAny(id, "reasoner", "thinking", "-r1", "r1-", "o1-", "o3-", "o4-mini", "qwq"))
                roles.Add("thinking");
            if (ContainsAny(id, "vision", "-vl", "vl-", "gpt-4o", "gpt-4.1", "gpt-5", "gemini",
                    "claude-3", "claude-sonnet", "claude-opus", "qwen-vl", "multimodal"))
                roles.Add("multimodal");
            if (!roles.Contains("speech") && !roles.Contains("image") && !roles.Contains("chat"))
                roles.Add("chat");
            return roles;
        }

        public static bool HasRole(LlmModelEntry model, string role)
        {
            if (model == null || string.IsNullOrWhiteSpace(role)) return false;
            if (model.roles == null) return false;
            foreach (var item in model.roles)
            {
                if (string.Equals(item, role, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool ContainsAny(string haystack, params string[] needles)
        {
            foreach (var needle in needles)
            {
                if (!string.IsNullOrEmpty(needle) && haystack.IndexOf(needle, StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        private static LlmProviderTemplate T(
            string key, string id, string type, string baseUrl, string model, float temperature)
        {
            return new LlmProviderTemplate
            {
                key = key,
                id = id,
                type = type,
                displayName = key,
                baseUrl = baseUrl,
                model = model,
                temperature = temperature,
                topP = 1f,
                maxTokens = 8192
            };
        }
    }

    public sealed class LlmProviderTemplate
    {
        public string key { get; set; }
        public string id { get; set; }
        public string type { get; set; }
        public string displayName { get; set; }
        public string baseUrl { get; set; }
        public string model { get; set; }
        public float temperature { get; set; }
        public float topP { get; set; }
        public int maxTokens { get; set; }
    }
}
