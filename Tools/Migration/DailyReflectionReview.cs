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
    public static partial class DayBuilder
    {
        public sealed class DailyReflection
        {
            public string summary;
            public List<int> cognition_refs;
        }

        private static async Task BuildDailyReflection(MigrationContext context, DailyReviewState state,
            List<CognitionSliceRecord> evidence, ILlmClient llm)
        {
            var input = new {
                day = state.Day, pair = context.RequirePair(), events = state.Events.Select(e => e.Summary),
                cognitions = evidence.Select((n, i) => new { cognition_ref = i + 1, summary = n.Summary,
                    slot = n.IdentitySlot, about = n.About, scope = n.Scope, exceptions = n.Exceptions })
            };
            var messages = new List<DeepSeekMessageData> {
                new("system", ReviewIdentity(state) + "【" + SubjectiveStage(state, UsesJointReview(state) ? 2 : 3, "日终回顾") + "】一天过去了，我想留下怎样的一段回想？把今天发生的事和由此新想到的东西，收拢成一段简短、连贯的第一人称回顾。" +
                    (UsesExplicitReview(state)
                        ? "从给出的经历与理解中，选出此刻仍牵动我的部分，连起发生的事、它让我怎样感受，以及我由此明白了什么。事实与转述沿用材料中的表达者、时间和确定程度；写理解的变化时，起点来自四卡或经历中明确表达过的认识，材料只呈现今天的一面时，就写今天我看见与感受到的这一面。让这段回想有具体依托，也让下一步的我感到此刻站在哪里。"
                        : "挑出真正留在心里的经历，用它串起今天的感受与理解；几件事带来同一种体会时，把体会说清楚就收住。让这段话读起来有今天的痕迹，也能让下一步的我感到此刻站在哪里。") +
                    "summary是一段自然文字，cognition_refs列出这段回顾实际采用的新理解编号，只有经历和感受时可为空。" +
                    "返回JSON {\"summary\":\"我的日终回顾\",\"cognition_refs\":[1]}。" + ReviewOutputContract(state)),
                new("user", TraceJson.ToJson(input))
            };
            var stamp = Digest(new { state.Stamp, messages, ids = evidence.Select(n => n.Id) });
            if (state.Reflection != null && state.ReflectionStamp == stamp) return;
            context.Migration.SetReviewStage(SubjectiveStage(state, UsesJointReview(state) ? 2 : 3, "日终回顾") + "：收拢今天的经历与新理解");
            var output = await DeepSeekStructuredOutputLogic.CompleteAsync<DailyReflection>(llm, messages, null,
                "请给出日终回顾。", CancellationToken.None, preserveValidContent: true, validationError: x => {
                    if (string.IsNullOrWhiteSpace(x.summary)) return "summary请填写一段完整的日终回顾。";
                    x.cognition_refs = x.cognition_refs?.Distinct().ToList() ?? new();
                    return ReferenceError(x.cognition_refs, evidence.Count, "cognition_refs", true);
                });
            state.Reflection = output;
            state.ReflectionStamp = stamp;
            SaveDaily(context, state);
        }

        private static string ReflectedRuntimePrompt(DailyReviewState state)
            => ReviewIdentity(state) + "【" + SubjectiveStage(state, UsesJointReview(state) ? 3 : 4, "更新当前状态") + "】读完这段日终回顾，体会我此刻的感受，以及我对自己、对方和我们有什么需要更新的理解。" +
                "身份卡提供我的出发点，reflection是今天沉淀下来的回想。cards列出有本轮依据、可更新的卡片及其用途。" +
                "每张需要更新的卡，围绕该卡最核心的认识写出现在成立的理解，意思落定就收住；当天的故事已经留在回顾里。body是该槽位更新后的正文，会替换当前卡片，保持原样的卡省略。" +
                "runtime的narrative写此刻心里停留的念头，mood写当下的情绪质感，relationship_lens写我此刻怎样感受我们之间的关系。各字段自然、简洁，各自表达自己的意思。" +
                "卡片依据由程序关联，输出slot和body即可；runtime按需给字段，省略或空字符串表示保持原样。" +
                "返回JSON {\"cards\":[{\"slot\":\"relation\",\"body\":\"我此刻对我们的理解\"}],\"runtime\":{\"narrative\":\"\",\"mood\":\"\",\"relationship_lens\":\"\"}}。" + ReviewOutputContract(state);
    }
}
