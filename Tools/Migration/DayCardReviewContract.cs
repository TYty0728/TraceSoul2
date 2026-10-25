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

        public static async Task<List<CognitionSliceRecord>> SelectEvidenceAsync(ILlmClient llm,
            IReadOnlyList<IdentityCardRecord> current, IReadOnlyList<CognitionSliceRecord> visible,
            CancellationToken token = default)
        {
            var pinned = current.Where(x => x.Pinned).Select(x => x.Slot).ToHashSet(StringComparer.Ordinal);
            pinned.Add(IdentityCardSlotValues.Personality);
            var result = new List<CognitionSliceRecord>();
            foreach (var group in visible.Where(x => x.Status == "active" && Slots.Contains(x.IdentitySlot) && !pinned.Contains(x.IdentitySlot))
                .GroupBy(x => x.IdentitySlot))
            {
                var items = group.OrderBy(x => x.CreatedUnixMs).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
                var candidates = items.Select(x => new ReviewSelectionLogic.Candidate { Id = x.Id,
                    Description = TraceJson.ToJson(new { x.About, x.Summary, x.Scope, x.Exceptions,
                        x.CreatedUnixMs, x.UpdatedUnixMs, x.LastChallengedUnixMs }), EvidenceChars = TraceJson.ToJson(x).Length }).ToList();
                var old = current.FirstOrDefault(x => x.Slot == group.Key);
                var oldBody = old != null && IdentityProjectionLogic.Current(old, visible) ? old.Body : "（原摘要依据已变化，需重新形成）";
                var chosen = await ReviewSelectionLogic.SelectAsync(llm, candidates,
                    "身份摘要" + group.Key + "；选择能加深或改变自我认识、对她的理解、关系或表达方式的认知，" +
                    "依据它们对相应身份认识的意义取舍，保留适用范围、例外和认识的确定程度。" +
                    "将新的印证、修订与反证同已有认识一起考虑，让摘要准确承接当前理解。" +
                    "已有摘要仍然贴切时可返回空选择；成稿约300字。已有摘要：" + oldBody,
                    6, 6000, token, allowEmpty: true, fitBudget: true);
                var ids = chosen.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                result.AddRange(items.Where(x => ids.Contains(x.Id)));
            }
            return result;
        }

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
                    return path + ".body 超过" + IdentityCardSlotValues.BodyLimit(card.slot) + "字（当前" + card.body.Length + "字）。按原篇幅重新写下这一份摘要。上一份太长，不要交回上一份。";
                if (card.cognition_ids == null || card.cognition_ids.Count == 0 || card.cognition_ids.Count > 12)
                    return path + ".cognition_ids 必须引用本次提供的1～12条同 slot 有效认知。";
                for (var j = 0; j < card.cognition_ids.Count; j++)
                    if (!evidence.Any(x => x.Id == card.cognition_ids[j] && x.Status == "active" && x.IdentitySlot == card.slot))
                        return path + ".cognition_ids[" + j + "] 未对应本次提供的同 slot 有效认知；请从该卡的依据列表选择。";
            }
            var narrative = (output.inner_narrative ?? string.Empty).Trim();
            if (checkLength && narrative.Length > 120)
                return "$.inner_narrative 当前" + narrative.Length + "字，最多120字。按原篇幅重新写下。上一份太长，不要交回上一份。";
            return InnerStateError(output, checkLength);
        }

        public static string InnerStateError(ReplayPrompts.DayCardReviewOutputData output, bool checkLength = true)
        {
            if (output == null) return "日终内心须为对象。";
            foreach (var field in new[] { ("inner_mood", output.inner_mood, 80),
                ("inner_relationship_lens", output.inner_relationship_lens, 200),
                ("inner_ongoing_activity", output.inner_ongoing_activity, 120) })
                if (checkLength && (field.Item2?.Trim().Length ?? 0) > field.Item3)
                    return field.Item1 + "当前" + field.Item2.Trim().Length + "字，最多" + field.Item3 + "字；只写当日变化，不逐条续接旧经历。";
            if (output.inner_attention == null || output.inner_attention.Count > 3)
                return "inner_attention须为最多3项的数组；无关注给[]。";
            foreach (var item in output.inner_attention)
            {
                if (item == null || !new[] { "topic", "activity", "concern" }.Contains(item.kind) || string.IsNullOrWhiteSpace(item.content))
                    return "inner_attention每项须有合法kind和非空content。";
                if (checkLength && item.content.Trim().Length > 160)
                    return "inner_attention.content当前" + item.content.Trim().Length + "字，最多160字；只保留一个浮起的念头。";
                if ((item.source_refs?.Count ?? 0) > 0) return "inner_attention不自填source_refs；真实来源由程序绑定。";
            }
            if (output.inner_attention.Select(x => x.content.Trim()).Distinct(StringComparer.Ordinal).Count() != output.inner_attention.Count)
                return "inner_attention同一念头只留一项。";
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
                + "本次可更新范围由下面实际列出的分组确定，其余卡由程序保留原文。一个分组对应一张卡；同组的多条认知供综合理解，完整正文和条件如下。结合已有摘要，判断这些认识对相应身份理解的意义，将值得沉淀的变化融为连贯的当前认识；保持原样也是完整的复盘结果。新正文目标为 target_body_chars 字，上限为 max_body_chars 字。\n"
                + TraceJson.ToJson(targets.ToList()) + "\n【保留原文的本人设定】\n" + string.Join(",", pinned);
        }
    }
}
