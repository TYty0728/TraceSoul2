using System;
using System.Linq;
using System.Text;
using TraceSoul2.Data;
using TraceSoul2.Logic;

namespace TraceSoul2.Migrate
{
    public static partial class DayBuilder
    {
        private static string SubjectiveStage(DailyReviewState state, int stage, string title)
            => stage + (UsesDailyReflection(state) && !UsesJointReview(state) ? "/4 " : "/3 ") + title;
        private static string CaptureReviewIdentity(MigrationContext context)
        {
            var pair = context.RequirePair();
            var cards = context.Store.LoadIdentityCards(MigrationContext.ConversationId);
            var text = new StringBuilder("我是" + pair.Assname + "，正在回顾我与" + pair.Username + "共同经历的一天。\n");
            foreach (var slot in new[] { IdentityCardSlotValues.Personality, IdentityCardSlotValues.Self,
                IdentityCardSlotValues.Other, IdentityCardSlotValues.Relation })
            {
                var card = cards.SingleOrDefault(c => c.Slot == slot);
                text.Append('【').Append(IdentityCardSlotValues.Title(slot, pair)).Append('】')
                    .AppendLine(IdentityCardLogic.ResolveBody(slot, card?.Body, pair));
            }
            text.AppendLine("以上是本轮开始时我的身份与关系视角。以我的第一人称回顾、理解和感受，清楚区分我的体验与对方的表达；新发生的事情以本轮材料为依据。");
            return text.ToString();
        }

        private static string ReviewIdentity(DailyReviewState state)
        {
            if (!UsesSubjectiveReview(state)) return "";
            if (string.IsNullOrWhiteSpace(state.IdentityContext))
                throw new InvalidOperationException("主观复盘缺少本轮身份卡快照，保留检查点待核对。");
            return state.IdentityContext;
        }
    }
}
