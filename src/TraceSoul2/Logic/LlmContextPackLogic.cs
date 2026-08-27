using System.Collections.Generic;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 上下文装配策略入口。当前所有渠道都走 Common；以后某家需要特殊布局时，
    /// 在 ResolvePack 与对应分支中添加实现，不让业务调用方感知供应商差异。
    /// </summary>
    public enum LlmContextPackKind
    {
        Common = 0,
        DeepSeek = 1,
        Kimi = 2,
        Glm = 3
    }

    public static class LlmContextPackLogic
    {
        public static LlmContextPackKind ResolvePack(ILlmClient llm)
        {
            // 暂时所有官网、中转和其他兼容模型都使用公共形状。
            // 后续只需按 OfficialLlmChannelLogic.Resolve(llm) 在这里选择专用实现。
            return LlmContextPackKind.Common;
        }

        public static string SharedSystem(ILlmClient llm, TraceTurnContext turn)
        {
            switch (ResolvePack(llm))
            {
                default:
                    return CommonContextPackLogic.SharedSystem(turn);
            }
        }

        public static List<DeepSeekMessageData> AssembleMind(
            ILlmClient llm,
            string sharedSystem,
            TraceTurnContext turn,
            string sharedMemory,
            string currentUserContent,
            string roleStable,
            string roleDynamic)
        {
            return Assemble(
                llm, sharedSystem, turn, sharedMemory, currentUserContent,
                CommonContextPackLogic.MindRoleHeader, roleStable, roleDynamic);
        }

        public static List<DeepSeekMessageData> AssembleExpress(
            ILlmClient llm,
            string sharedSystem,
            TraceTurnContext turn,
            string sharedMemory,
            string currentUserContent,
            string roleStable,
            string roleDynamic)
        {
            return Assemble(
                llm, sharedSystem, turn, sharedMemory, currentUserContent,
                CommonContextPackLogic.ExpressRoleHeader, roleStable, roleDynamic);
        }

        /// <summary>
        /// 通用形状：稳定 system → 量化对齐的历史窗口 → 专属稳定指令 → 共享记忆 →
        /// 轮内动态指令 → 当前用户消息。插件只更换 roleHeader / 稳定与动态指令。
        /// 插件旧用法（单段 roleInstructions）走 ILlmContextAssembler.Assemble，
        /// 作为 roleDynamic 传入，消息顺序与旧形状一致。
        /// </summary>
        public static List<DeepSeekMessageData> Assemble(
            ILlmClient llm,
            string sharedSystem,
            TraceTurnContext turn,
            string sharedMemory,
            string currentUserContent,
            string roleHeader,
            string roleStable,
            string roleDynamic)
        {
            switch (ResolvePack(llm))
            {
                default:
                    return CommonContextPackLogic.Assemble(
                        sharedSystem, turn, sharedMemory, currentUserContent,
                        roleHeader, roleStable, roleDynamic, IncludeAssistantReasoning(llm));
            }
        }

        public static List<DeepSeekMessageData> AssembleReview(
            ILlmClient llm,
            string sharedSystem,
            string roleInstructions,
            string userAsk)
        {
            switch (ResolvePack(llm))
            {
                default:
                    return CommonContextPackLogic.AssembleReview(sharedSystem, roleInstructions, userAsk);
            }
        }

        /// <summary>Kimi 官网当前支持显式会话缓存键；其他渠道使用各自的隐式缓存。</summary>
        public static string BuildPromptCacheKey(ILlmClient llm, string conversationId)
        {
            return OfficialLlmChannelLogic.Resolve(llm) == OfficialLlmChannel.Kimi
                ? CommonContextPackLogic.BuildConversationCacheKey(conversationId)
                : null;
        }

        /// <summary>K3 多轮要求回传完整 assistant 推理；不把该字段扩散给其他兼容接口。</summary>
        private static bool IncludeAssistantReasoning(ILlmClient llm)
        {
            return OfficialLlmChannelLogic.Resolve(llm) == OfficialLlmChannel.Kimi;
        }
    }
}
