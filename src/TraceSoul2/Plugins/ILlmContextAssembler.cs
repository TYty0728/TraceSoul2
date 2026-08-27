using System.Collections.Generic;
using TraceSoul2.Data;
using TraceSoul2.Manager;

namespace TraceSoul2.Plugins
{
    /// <summary>
    /// 公共上下文装配器。插件的专用对话模型必须走这里，才能与心智/开口共享稳定前缀：
    /// system（身份卡）→ 历史 → 共享记忆 → 专属指令 → 当前用户消息。
    /// 只允许在专属指令处分叉，不要自己截断身份卡或把对话压成「名字：原文」。
    /// </summary>
    public interface ILlmContextAssembler
    {
        string SharedSystem(ILlmClient llm, TraceTurnContext turn);

        List<DeepSeekMessageData> Assemble(
            ILlmClient llm,
            TraceTurnContext turn,
            string sharedMemory,
            string currentUserContent,
            string roleHeader,
            string roleInstructions);

        string BuildPromptCacheKey(ILlmClient llm, string conversationId);
    }
}
