using System.Collections.Generic;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.Logic
{
    /// <summary>宿主注入给插件的公共上下文装配器，转发 LlmContextPackLogic。</summary>
    public sealed class LlmContextAssembler : ILlmContextAssembler
    {
        public string SharedSystem(ILlmClient llm, TraceTurnContext turn)
        {
            return LlmContextPackLogic.SharedSystem(llm, turn);
        }

        public List<DeepSeekMessageData> Assemble(
            ILlmClient llm,
            TraceTurnContext turn,
            string sharedMemory,
            string currentUserContent,
            string roleHeader,
            string roleInstructions)
        {
            // 插件沿用单段指令的旧约定：整段作为轮内动态指令传入，
            // 消息顺序与旧形状一致（历史 → 共享记忆 → 专属指令）。
            return LlmContextPackLogic.Assemble(
                llm,
                SharedSystem(llm, turn),
                turn,
                sharedMemory,
                currentUserContent,
                roleHeader,
                string.Empty,
                roleInstructions);
        }

        public string BuildPromptCacheKey(ILlmClient llm, string conversationId)
        {
            return LlmContextPackLogic.BuildPromptCacheKey(llm, conversationId);
        }
    }
}
