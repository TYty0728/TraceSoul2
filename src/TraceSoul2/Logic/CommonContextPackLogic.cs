using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 所有 LLM 默认使用的公共上下文形状。公共段保持字节级稳定，心智、开口与复盘
    /// 只在最后的专属指令处分叉。供应商特有优化由 LlmContextPackLogic 路由，不写进这里。
    /// </summary>
    public static class CommonContextPackLogic
    {
        public const string MemoryHeader = "【相关记忆】";
        public const string MindRoleHeader = "【心智】";
        public const string ExpressRoleHeader = "【开口】";
        public const string ReviewRoleHeader = "【复盘】";

        public static string BuildConversationCacheKey(string conversationId)
        {
            var id = (conversationId ?? string.Empty).Trim();
            if (id.Length == 0) id = "default";
            return "tracesoul2:" + id;
        }

        public static string SharedSystem(TraceTurnContext turn)
        {
            if (turn == null || turn.Services == null || turn.Services.Storage == null)
                return string.Empty;
            var pair = turn.Services.Storage.LoadPairIdentity();
            var cards = turn.Services.Storage.LoadIdentityCards(turn.ConversationId);
            return IdentityCardLogic.FormatForExpressor(cards, pair);
        }

        public static List<DeepSeekMessageData> AssembleMind(
            string sharedSystem,
            TraceTurnContext turn,
            string sharedMemory,
            string currentUserContent,
            string roleInstructions,
            bool includeAssistantReasoning = false)
        {
            return Assemble(
                sharedSystem,
                turn,
                sharedMemory,
                currentUserContent,
                MindRoleHeader,
                roleInstructions,
                includeAssistantReasoning);
        }

        public static List<DeepSeekMessageData> AssembleExpress(
            string sharedSystem,
            TraceTurnContext turn,
            string sharedMemory,
            string currentUserContent,
            string roleInstructions,
            bool includeAssistantReasoning = false)
        {
            return Assemble(
                sharedSystem,
                turn,
                sharedMemory,
                currentUserContent,
                ExpressRoleHeader,
                roleInstructions,
                includeAssistantReasoning);
        }

        public static List<DeepSeekMessageData> AssembleReview(
            string sharedSystem,
            string roleInstructions,
            string userAsk)
        {
            var messages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", sharedSystem ?? string.Empty)
            };
            AppendNamed(messages, ReviewRoleHeader, roleInstructions);
            var ask = (userAsk ?? string.Empty).Trim();
            if (ask.Length > 0)
                messages.Add(new DeepSeekMessageData("user", ask));
            return messages;
        }

        /// <summary>
        /// 公共形状：稳定 system → 历史 → 共享记忆 → 专属指令 → 当前用户消息。
        /// R0、会话断层与历史基底后续都应在“历史”位置实现，不改变调用方。
        /// </summary>
        public static List<DeepSeekMessageData> Assemble(
            string sharedSystem,
            TraceTurnContext turn,
            string sharedMemory,
            string currentUserContent,
            string roleHeader,
            string roleInstructions,
            bool includeAssistantReasoning = false)
        {
            var messages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", sharedSystem ?? string.Empty)
            };
            messages.AddRange(BuildRecentChatHistory(turn, includeAssistantReasoning));
            AppendNamed(messages, MemoryHeader, sharedMemory);
            AppendNamed(messages, roleHeader, roleInstructions);
            if (!HeartbeatLogic.IsHeartbeatContent(currentUserContent) &&
                !string.IsNullOrWhiteSpace(currentUserContent))
                messages.Add(new DeepSeekMessageData("user", currentUserContent));
            return messages;
        }

        public static int SharedPrefixCount(
            IReadOnlyList<DeepSeekMessageData> left,
            IReadOnlyList<DeepSeekMessageData> right)
        {
            if (left == null || right == null) return 0;
            var n = Math.Min(left.Count, right.Count);
            for (var i = 0; i < n; i++)
            {
                if (!SameMessage(left[i], right[i])) return i;
            }
            return n;
        }

        internal static List<DeepSeekMessageData> BuildRecentChatHistory(
            TraceTurnContext turn,
            bool includeAssistantReasoning = false)
        {
            var result = new List<DeepSeekMessageData>();
            if (turn == null || turn.RawHistoryLimit <= 0 || turn.RecentMoments == null ||
                turn.RecentMoments.Count == 0 || turn.Services == null || turn.Services.Storage == null)
                return result;
            var pair = turn.Services.Storage.LoadPairIdentity();
            var lines = turn.RecentMoments
                .Where(x => x != null &&
                            (pair.IsHumanMoment(x.Role) || pair.IsCompanionMoment(x.Role)) &&
                            !string.IsNullOrWhiteSpace(x.Content) &&
                            !MindLogic.IsOutboundProtocolMoment(x.Content))
                .TakeLast(turn.RawHistoryLimit)
                .ToList();
            foreach (var item in lines)
            {
                var role = pair.IsHumanMoment(item.Role) ? "user" : "assistant";
                var text = item.Content.Trim();
                var reasoning = includeAssistantReasoning ? ExtractReasoningContent(item) : null;
                if (result.Count > 0 &&
                    string.Equals(result[result.Count - 1].role, role, StringComparison.Ordinal) &&
                    string.IsNullOrWhiteSpace(result[result.Count - 1].reasoning_content))
                {
                    result[result.Count - 1].content += "\n" + text;
                    continue;
                }
                result.Add(new DeepSeekMessageData(role, text) { reasoning_content = reasoning });
            }
            return result;
        }

        private static void AppendNamed(List<DeepSeekMessageData> messages, string header, string body)
        {
            var text = (body ?? string.Empty).Trim();
            if (text.Length == 0) return;
            messages.Add(new DeepSeekMessageData("user", header + "\n" + text));
        }

        private static bool SameMessage(DeepSeekMessageData left, DeepSeekMessageData right)
        {
            if (left == null || right == null) return false;
            if (!string.Equals(left.role ?? string.Empty, right.role ?? string.Empty, StringComparison.Ordinal))
                return false;
            if (!string.Equals(left.content ?? string.Empty, right.content ?? string.Empty, StringComparison.Ordinal))
                return false;
            return string.Equals(
                left.reasoning_content ?? string.Empty,
                right.reasoning_content ?? string.Empty,
                StringComparison.Ordinal);
        }

        private static string ExtractReasoningContent(MomentRecord moment)
        {
            if (moment == null || string.IsNullOrWhiteSpace(moment.PayloadJson)) return null;
            try
            {
                var payload = TraceSoul2.Util.TraceJson.FromJson<AssistantPayloadData>(moment.PayloadJson);
                if (payload == null || string.IsNullOrWhiteSpace(payload.reasoning_content))
                    return null;
                return payload.reasoning_content.Trim();
            }
            catch
            {
                return null;
            }
        }

        [Serializable]
        private sealed class AssistantPayloadData
        {
            public string reasoning_content { get; set; }
        }
    }
}
