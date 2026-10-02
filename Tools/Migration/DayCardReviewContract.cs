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
                        + "将相关理解综合成一个约" + IdentityCardSlotValues.BodyTarget(card.slot)
                        + "字的完整 body，合并所用 cognition_ids，其他卡保持不变；不要丢掉有效依据来凑空数组。";
                seen.Add(card.slot, i);
                if (current.Any(x => x.Slot == card.slot && x.Pinned))
                    return path + ".slot 是本人固定的卡，请移除此更新；其他可更新卡仍按依据处理。";
                if (string.IsNullOrWhiteSpace(card.body)) return path + ".body 不能为空。";
                if (checkLength && card.body.Length > IdentityCardSlotValues.BodyLimit(card.slot))
                    return path + ".body 超过" + IdentityCardSlotValues.BodyLimit(card.slot) + "字的异常长度边界（当前" + card.body.Length + "字），请围绕核心理解凝练成完整摘要。";
                if (card.cognition_ids == null || card.cognition_ids.Count == 0 || card.cognition_ids.Count > 12)
                    return path + ".cognition_ids 必须引用本次提供的1～12条同 slot 有效认知。";
                for (var j = 0; j < card.cognition_ids.Count; j++)
                    if (!evidence.Any(x => x.Id == card.cognition_ids[j] && x.Status == "active" && x.IdentitySlot == card.slot))
                        return path + ".cognition_ids[" + j + "] 未对应本次提供的同 slot 有效认知；请从该卡的依据列表选择。";
            }
            return null;
        }

        public sealed class CondensedBody { public string slot; public string body; }
        public sealed class CondensedBodies { public List<CondensedBody> cards; }

        /// <summary>结构及来源先校验；长度是独立的文字整理步骤，只有超长正文进入小上下文。</summary>
        public static async Task FitBodiesAsync(ILlmClient llm, ReplayPrompts.DayCardReviewOutputData output,
            IReadOnlyList<IdentityCardRecord> current, IReadOnlyList<CognitionSliceRecord> evidence,
            CancellationToken token)
        {
            var structuralError = StructuralError(output, current, evidence);
            if (structuralError != null) throw new InvalidOperationException(structuralError);
            var pending = output.cards.Where(c => c.body.Length > IdentityCardSlotValues.BodyLimit(c.slot)).ToList();
            if (pending.Count == 0) return;
            var messages = new List<DeepSeekMessageData> {
                new("system", "把已有的身份成长摘要凝练得更简洁。保持原文的含义、关系中的条件和重要差别，合并重复表达，保留连贯的第一人称。" +
                    "这里只整理给出的正文，既有认知依据由程序保留。每张摘要以目标长度为宜，完整表达后收尾。" +
                    "输出 JSON：{\"cards\":[{\"slot\":\"原 slot\",\"body\":\"凝练后的完整正文\"}]}；每个给出的 slot 对应一项。"),
                new("user", TraceJson.ToJson(pending.Select(c => new { c.slot, c.body, current_chars = c.body.Length,
                    target_chars = IdentityCardSlotValues.BodyTarget(c.slot), max_chars = IdentityCardSlotValues.BodyLimit(c.slot) }).ToList()))
            };
            string Error(CondensedBodies result)
            {
                if (result?.cards == null || result.cards.Count != pending.Count)
                    return "$.cards 应与给出的摘要逐一对应。";
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < result.cards.Count; i++)
                {
                    var card = result.cards[i];
                    var path = "$.cards[" + i + "]";
                    if (card == null || !pending.Any(x => x.slot == card.slot) || !seen.Add(card.slot))
                        return path + ".slot 应使用给出的 slot，每项一次。";
                    var max = IdentityCardSlotValues.BodyLimit(card.slot);
                    if (string.IsNullOrWhiteSpace(card.body)) return path + ".body 应为完整的摘要正文。";
                    if (card.body.Length > max) return path + ".body 当前" + card.body.Length + "字，上限" + max +
                        "字；请凝练到约" + IdentityCardSlotValues.BodyTarget(card.slot) + "字，保留重要含义和完整结尾。";
                }
                return null;
            }
            var fitted = await DeepSeekStructuredOutputLogic.CompleteAsync<CondensedBodies>(llm, messages, null,
                "摘要精炼结果不符合长度要求。", token, validationError: Error);
            // 全部通过后才替换正文；slot、reason、cognition_ids 和内心输出都沿用原结果。
            foreach (var card in fitted.cards) pending.Single(x => x.slot == card.slot).body = card.body;
        }

        public static string EvidenceContext(IReadOnlyList<IdentityCardRecord> current, IReadOnlyList<CognitionSliceRecord> evidence)
        {
            var pinned = current.Where(x => x.Pinned).Select(x => x.Slot).ToHashSet(StringComparer.Ordinal);
            pinned.Add(IdentityCardSlotValues.Personality);
            var targets = evidence.Where(x => x.Status == "active" && Slots.Contains(x.IdentitySlot) && !pinned.Contains(x.IdentitySlot))
                .GroupBy(x => x.IdentitySlot).Select(group => new { slot = group.Key,
                    target_body_chars = IdentityCardSlotValues.BodyTarget(group.Key), cognitions = group.ToList() });
            return "\n【成长摘要与可参考的认识，按 slot 分组】\n"
                + "一个分组对应一张卡；同组的多条认知供综合理解。对照已有摘要，选择值得沉淀的变化；仍然贴切的认识继续保留。\n"
                + TraceJson.ToJson(targets.ToList()) + "\n【保留原文的本人设定】\n" + string.Join(",", pinned);
        }
    }
}
