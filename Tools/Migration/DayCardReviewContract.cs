using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;
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
                        + "将相关理解综合成一个不超过" + IdentityCardSlotValues.BodyLimit(card.slot)
                        + "字的 body，合并所用 cognition_ids，其他卡保持不变；不要丢掉有效依据来凑空数组。";
                seen.Add(card.slot, i);
                if (current.Any(x => x.Slot == card.slot && x.Pinned))
                    return path + ".slot 是本人固定的卡，请移除此更新；其他可更新卡仍按依据处理。";
                if (string.IsNullOrWhiteSpace(card.body)) return path + ".body 不能为空。";
                if (card.body.Length > IdentityCardSlotValues.BodyLimit(card.slot))
                    return path + ".body 超过" + IdentityCardSlotValues.BodyLimit(card.slot) + "字，须综合精简后完整输出。";
                if (card.cognition_ids == null || card.cognition_ids.Count == 0 || card.cognition_ids.Count > 12)
                    return path + ".cognition_ids 必须引用本次提供的1～12条同 slot 有效认知。";
                for (var j = 0; j < card.cognition_ids.Count; j++)
                    if (!evidence.Any(x => x.Id == card.cognition_ids[j] && x.Status == "active" && x.IdentitySlot == card.slot))
                        return path + ".cognition_ids[" + j + "] 未对应本次提供的同 slot 有效认知；请从该卡的依据列表选择。";
            }
            return null;
        }

        public static string EvidenceContext(IReadOnlyList<IdentityCardRecord> current, IReadOnlyList<CognitionSliceRecord> evidence)
        {
            var pinned = current.Where(x => x.Pinned).Select(x => x.Slot).ToHashSet(StringComparer.Ordinal);
            var targets = evidence.Where(x => x.Status == "active" && Slots.Contains(x.IdentitySlot) && !pinned.Contains(x.IdentitySlot))
                .GroupBy(x => x.IdentitySlot).Select(group => new { slot = group.Key,
                    max_body_chars = IdentityCardSlotValues.BodyLimit(group.Key), cognitions = group.ToList() });
            return "\n【本次可更新的身份卡与依据，按 slot 分组】\n"
                + "一个分组对应一张卡；同组的多条认知用于综合理解，不能逐条生成同名卡。只返回有实际变化的卡，不要求固定张数。\n"
                + TraceJson.ToJson(targets.ToList()) + "\n【本人固定，不能覆盖的卡】\n" + string.Join(",", pinned);
        }
    }
}
