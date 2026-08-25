using System;
using TraceSoul2.Data;
using TraceSoul2.Manager;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 识别三条官网渠道，供私有请求字段、usage 解析等传输兼容使用。
    /// 上下文形状由 LlmContextPackLogic 独立路由，当前所有渠道默认走 Common。
    /// </summary>
    public enum OfficialLlmChannel
    {
        None = 0,
        DeepSeek = 1,
        Kimi = 2,
        Glm = 3
    }

    public static class OfficialLlmChannelLogic
    {
        public static OfficialLlmChannel Resolve(ILlmClient llm)
        {
            var endpoint = llm as ILlmEndpoint;
            return Resolve(endpoint == null ? null : endpoint.BaseUrl);
        }

        public static OfficialLlmChannel Resolve(DeepSeekConfigData config)
        {
            return Resolve(config == null ? null : config.BaseUrl);
        }

        public static OfficialLlmChannel Resolve(string baseUrl)
        {
            var url = (baseUrl ?? string.Empty).Trim();
            if (url.Length == 0) return OfficialLlmChannel.None;
            if (Contains(url, "deepseek.com")) return OfficialLlmChannel.DeepSeek;
            if (Contains(url, "moonshot.cn") ||
                Contains(url, "moonshot.ai") ||
                Contains(url, "api.kimi.com") ||
                Contains(url, "api.kimi.ai"))
                return OfficialLlmChannel.Kimi;
            if (Contains(url, "bigmodel.cn") || Contains(url, "zhipuai.cn"))
                return OfficialLlmChannel.Glm;
            return OfficialLlmChannel.None;
        }

        private static bool Contains(string url, string needle)
        {
            return url.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
