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
    /// 缓存视角的形状约定：稳定 system → 量化对齐的历史窗口 → 专属稳定指令 →
    /// 共享记忆（每轮检索，会变化）→ 轮内动态指令（时间/心智卡等）→ 当前用户消息。
    /// </summary>
    public static class CommonContextPackLogic
    {
        public const string MemoryHeader = "【相关记忆】";
        public const string MindRoleHeader = "【心智】";
        public const string ExpressRoleHeader = "【开口】";
        public const string ReviewRoleHeader = "【复盘】";

        /// <summary>
        /// 历史窗口默认滑动粒度（Moment 条数）。前缀缓存要求历史头部字节级稳定，
        /// 逐条滑动会让每条新消息都打断前缀；对齐后每攒满一个粒度才整体滑动一次。
        /// 窗口长度在 [Max-Align+1, Max] 之间浮动。Align=1 则每轮固定 Max 条。
        /// </summary>
        public const int HistoryWindowAlign = 4;
        public const int HistoryWindowMaxCap = 100;

        /// <summary>最高条数、滑动条数、由此推出的窗口下限。</summary>
        public sealed class HistoryWindow
        {
            public int Max;
            public int Align;
            public int Min;
        }

        /// <summary>
        /// 定参是最高条数和滑动条数。下限 = 最高 - 滑动 + 1。
        /// Max=0 关闭历史；Align≤0 用默认 4；Align 夹到 [1, Max]。
        /// </summary>
        public static HistoryWindow NormalizeHistoryWindow(int max, int align)
        {
            max = Math.Max(0, Math.Min(HistoryWindowMaxCap, max));
            if (max == 0)
                return new HistoryWindow { Max = 0, Align = 1, Min = 0 };
            if (align <= 0) align = HistoryWindowAlign;
            align = Math.Max(1, Math.Min(max, align));
            return new HistoryWindow { Max = max, Align = align, Min = max - align + 1 };
        }

        /// <summary>
        /// 旧设置里 contextInjectionCount 是窗口下限（默认 6，且曾被夹到 6）。
        /// 未写过最高条数时：Max = Min + Align - 1，6/4 → 9。
        /// </summary>
        public static HistoryWindow FromLegacyInjectionCount(int min, int align)
        {
            min = Math.Max(0, Math.Min(HistoryWindowMaxCap, min));
            if (min <= 0) return NormalizeHistoryWindow(0, align);
            if (align <= 0) align = HistoryWindowAlign;
            return NormalizeHistoryWindow(min + align - 1, align);
        }

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
            string roleStable,
            string roleDynamic,
            bool includeAssistantReasoning = false)
        {
            return Assemble(
                sharedSystem,
                turn,
                sharedMemory,
                currentUserContent,
                MindRoleHeader,
                roleStable,
                roleDynamic,
                includeAssistantReasoning);
        }

        public static List<DeepSeekMessageData> AssembleExpress(
            string sharedSystem,
            TraceTurnContext turn,
            string sharedMemory,
            string currentUserContent,
            string roleStable,
            string roleDynamic,
            bool includeAssistantReasoning = false)
        {
            return Assemble(
                sharedSystem,
                turn,
                sharedMemory,
                currentUserContent,
                ExpressRoleHeader,
                roleStable,
                roleDynamic,
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
        /// 公共形状：稳定 system → 量化对齐的历史窗口 → 专属稳定指令 → 共享记忆 →
        /// 轮内动态指令 → 当前用户消息。前缀缓存按字节级前缀匹配计费，越稳定的内容越靠前；
        /// 外部插件沿用旧约定时 roleStable 传空、roleInstructions 即为 roleDynamic，
        /// 消息顺序退化为旧形状，行为不变。
        /// </summary>
        public static List<DeepSeekMessageData> Assemble(
            string sharedSystem,
            TraceTurnContext turn,
            string sharedMemory,
            string currentUserContent,
            string roleHeader,
            string roleStable,
            string roleDynamic,
            bool includeAssistantReasoning = false)
        {
            var messages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", sharedSystem ?? string.Empty)
            };
            messages.AddRange(BuildRecentChatHistory(turn, includeAssistantReasoning));
            // 角色头贴在第一个非空指令段上：新用法归稳定段，插件旧用法（仅动态段）归动态段。
            var stable = (roleStable ?? string.Empty).Trim();
            var dynamic = (roleDynamic ?? string.Empty).Trim();
            if (stable.Length > 0) AppendNamed(messages, roleHeader, stable);
            AppendNamed(messages, MemoryHeader, sharedMemory);
            if (dynamic.Length > 0)
                AppendNamed(messages, stable.Length > 0 ? null : roleHeader, dynamic);
            if (!HeartbeatLogic.IsHeartbeatContent(currentUserContent) &&
                !NightResidueLogic.LooksLike(currentUserContent) &&
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

        /// <summary>
        /// 历史窗口起点按滑动条数量化对齐：新消息进来时起点不动，攒满一个粒度才整体滑动，
        /// 保证相邻轮次的历史前缀字节级一致。窗口实际长度在 [min, min+align-1] 即 [min, max] 之间。
        /// </summary>
        internal static int AlignedWindowStart(int total, int min)
        {
            return AlignedWindowStart(total, min, HistoryWindowAlign);
        }

        internal static int AlignedWindowStart(int total, int min, int align)
        {
            if (total <= min || min <= 0) return 0;
            if (align <= 0) align = HistoryWindowAlign;
            return ((total - min) / align) * align;
        }

        /// <summary>
        /// 时间线尾部应取的对话条数。必须用「全部对话条数」计算；
        /// 若先截成 min+Align-1 再算起点，起点永远是 0，历史第一条每轮都换。
        /// </summary>
        internal static int AlignedWindowTake(int total, int min)
        {
            return AlignedWindowTake(total, min, HistoryWindowAlign);
        }

        internal static int AlignedWindowTake(int total, int min, int align)
        {
            if (min <= 0 || total <= 0) return 0;
            return total - AlignedWindowStart(total, min, align);
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
            var filtered = turn.RecentMoments
                .Where(x => x != null &&
                            (pair.IsHumanMoment(x.Role) || pair.IsCompanionMoment(x.Role)) &&
                            !string.IsNullOrWhiteSpace(x.Content) &&
                            !MindLogic.IsOutboundProtocolMoment(x.Content))
                .ToList();
            var align = turn.HistoryWindowAlign > 0 ? turn.HistoryWindowAlign : HistoryWindowAlign;
            var lines = filtered.Skip(AlignedWindowStart(filtered.Count, turn.RawHistoryLimit, align)).ToList();
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
            messages.Add(new DeepSeekMessageData("user",
                string.IsNullOrEmpty(header) ? text : header + "\n" + text));
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
