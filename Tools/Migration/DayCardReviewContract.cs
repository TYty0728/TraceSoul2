using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Util;

namespace TraceSoul2.Migrate
{
    /// <summary>日终卡片按 slot 更新；一张摘要可以引用多条认知，不能按认知条数重复写卡。</summary>
    public static class DayCardReviewContract
    {
        private static readonly string[] Slots = { IdentityCardSlotValues.Personality, IdentityCardSlotValues.Self,
            IdentityCardSlotValues.Other, IdentityCardSlotValues.Relation, IdentityCardSlotValues.ExpressionHabit };

        public static string ValidationError(ReplayPrompts.DayCardReviewOutputData output,
            IReadOnlyList<IdentityCardRecord> current, IReadOnlyList<CognitionSliceRecord> evidence)
            => Validate(output, current, evidence, true);

        public static string StructuralError(ReplayPrompts.DayCardReviewOutputData output,
            IReadOnlyList<IdentityCardRecord> current, IReadOnlyList<CognitionSliceRecord> evidence)
            => Validate(output, current, evidence, false);

        /// <summary>程序确定可写目标；没有有效依据的卡继续保留原文，其他卡仍严格验证。</summary>
        public static List<string> PreserveUnavailableCards(ReplayPrompts.DayCardReviewOutputData output,
            IReadOnlyList<IdentityCardRecord> current, IReadOnlyList<CognitionSliceRecord> evidence)
        {
            if (output?.cards == null) return new();
            var pinned = current.Where(x => x.Pinned).Select(x => x.Slot).ToHashSet(StringComparer.Ordinal);
            pinned.Add(IdentityCardSlotValues.Personality);
            var eligible = evidence.Where(x => x.Status == "active" && Slots.Contains(x.IdentitySlot) && !pinned.Contains(x.IdentitySlot))
                .Select(x => x.IdentitySlot).ToHashSet(StringComparer.Ordinal);
            var preserved = output.cards.Where(x => x != null && Slots.Contains(x.slot) && !eligible.Contains(x.slot))
                .Select(x => x.slot).Distinct(StringComparer.Ordinal).ToList();
            // 未知slot、空对象、可写卡中的跨组/未知引用仍交给原校验，不猜测替换依据。
            output.cards.RemoveAll(x => x != null && preserved.Contains(x.slot));
            return preserved;
        }

        private static string Validate(ReplayPrompts.DayCardReviewOutputData output,
            IReadOnlyList<IdentityCardRecord> current, IReadOnlyList<CognitionSliceRecord> evidence, bool checkLength)
        {
            if (output == null) return "$ 必须是完整的身份摘要复盘对象。";
            if (output.cards == null) return "$.cards 必须显式给数组；无更新请给 []，不能省略或给 null。";
            if (output.cards.Count > Slots.Length) return "$.cards 至多5项，每种 slot 至多一项。";
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < output.cards.Count; i++)
            {
                var card = output.cards[i];
                var path = "$.cards[" + i + "]";
                if (card == null) return path + " 必须是卡片对象，不能为 null。";
                if (!Slots.Contains(card.slot)) return path + ".slot 必须是 personality/self/other/relation/expression_habit 之一。";
                if (seen.TryGetValue(card.slot, out var first))
                    return path + ".slot 与 $.cards[" + first + "].slot 重复（" + card.slot + "）。同一卡只输出一项；"
                        + "将相关理解综合成一份约" + IdentityCardSlotValues.BodyTarget(card.slot)
                        + "字、最多" + IdentityCardSlotValues.BodyLimit(card.slot)
                        + "字的完整 body，合并所用 cognition_ids，其他卡保持不变；不要丢掉有效依据来凑空数组。";
                seen.Add(card.slot, i);
                if (current.Any(x => x.Slot == card.slot && x.Pinned))
                    return path + ".slot 是本人固定的卡，请移除此更新；其他可更新卡仍按依据处理。";
                if (string.IsNullOrWhiteSpace(card.body)) return path + ".body 不能为空。";
                if (checkLength && card.body.Length > IdentityCardSlotValues.BodyLimit(card.slot))
                    return path + ".body 超过" + IdentityCardSlotValues.BodyLimit(card.slot) + "字（当前" + card.body.Length + "字）。按原篇幅重新写下这一份摘要，不要截断。";
                if (card.cognition_ids == null || card.cognition_ids.Count == 0 || card.cognition_ids.Count > 12)
                    return path + ".cognition_ids 必须引用本次提供的1～12条同 slot 有效认知。";
                for (var j = 0; j < card.cognition_ids.Count; j++)
                    if (!evidence.Any(x => x.Id == card.cognition_ids[j] && x.Status == "active" && x.IdentitySlot == card.slot))
                        return path + ".cognition_ids[" + j + "] 未对应本次提供的同 slot 有效认知；请从该卡的依据列表选择。";
            }
            var narrative = (output.inner_narrative ?? string.Empty).Trim();
            if (checkLength && narrative.Length > 120)
                return "$.inner_narrative 当前" + narrative.Length + "字，最多120字。按原篇幅重新写下，不要截断。";
            return null;
        }

        /// <summary>篇幅已在原指令里。超限由同一次生成重写，这里不删卡、不截断。</summary>
        public static Task<List<string>> FitBodiesAsync(ILlmClient llm, ReplayPrompts.DayCardReviewOutputData output,
            IReadOnlyList<IdentityCardRecord> current, IReadOnlyList<CognitionSliceRecord> evidence,
            CancellationToken token)
        {
            _ = llm;
            if (token.IsCancellationRequested) return Task.FromCanceled<List<string>>(token);
            var error = ValidationError(output, current, evidence);
            if (error != null)
                return Task.FromException<List<string>>(new InvalidOperationException(error));
            return Task.FromResult(new List<string>());
        }

        public static string EvidenceContext(IReadOnlyList<IdentityCardRecord> current, IReadOnlyList<CognitionSliceRecord> evidence)
        {
            var pinned = current.Where(x => x.Pinned).Select(x => x.Slot).ToHashSet(StringComparer.Ordinal);
            pinned.Add(IdentityCardSlotValues.Personality);
            var targets = evidence.Where(x => x.Status == "active" && Slots.Contains(x.IdentitySlot) && !pinned.Contains(x.IdentitySlot))
                .GroupBy(x => x.IdentitySlot).Select(group => new { slot = group.Key,
                    target_body_chars = IdentityCardSlotValues.BodyTarget(group.Key),
                    max_body_chars = IdentityCardSlotValues.BodyLimit(group.Key), cognitions = group.ToList() });
            return "\n【成长摘要与可参考的认识，按 slot 分组】\n"
                + "本次可更新范围由下面实际列出的分组确定，其余卡由程序保留原文。一个分组对应一张卡；同组的多条认知供综合理解。对照已有摘要，选择值得沉淀的变化；仍然贴切的认识继续保留。新正文按 target_body_chars 写下，不超过 max_body_chars，写完就是这份摘要。\n"
                + TraceJson.ToJson(targets.ToList()) + "\n【保留原文的本人设定】\n" + string.Join(",", pinned);
        }
    }
}
