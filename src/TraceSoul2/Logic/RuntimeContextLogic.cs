using System;
using System.Linq;
using System.Text;
using TraceSoul2.Data;
using TraceSoul2.Plugins;
using TraceSoul2.Prompts;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>当下状态唯一提示视图：时间 → 环境 → 生活 → 内心 → 当天经历。没有旧工具协议。</summary>
    public static class RuntimeContextLogic
    {
        public static string State(TraceTurnContext turn)
        {
            var builder = new StringBuilder("【当前状态】\n");
            var time = turn.Workspace.ContextBlocks.FirstOrDefault(x => x?.FacetId == "time.context")?.Content;
            builder.AppendLine(string.IsNullOrWhiteSpace(time) ? "现在是" + TimeLanguageUtil.NaturalNow(DateTimeOffset.Now) + "。" : time.Trim());
            var environment = turn.Environment;
            if (environment != null)
            {
                builder.AppendLine(environment.Visibility == "private"
                    ? "环境：私密。已确认只有我与专属用户单独相处。"
                    : "环境：公开。此处不具有我与专属用户之间的私密权限。");
                builder.Append("所在：").Append(environment.PlatformId).Append("；会话：")
                    .AppendLine(string.IsNullOrWhiteSpace(environment.SessionId) ? "未确认" : environment.SessionId);
                builder.Append("交流方式：").Append(environment.SessionType == "group" ? "多人共同交流" :
                    environment.SessionType == "private" ? "一对一交流" : "受众范围未确认").AppendLine("。");
                if (EnvironmentLogic.IsHumanInput(turn))
                    builder.Append("发言者：").Append(string.IsNullOrWhiteSpace(environment.SpeakerName) ? "未具名参与者" : environment.SpeakerName)
                        .Append("；账号：").Append(environment.SpeakerId ?? "未确认")
                        .Append(environment.SpeakerIsOwner ? "（已绑定的专属用户）" : "（其他人或身份未确认）")
                        .AppendLine(environment.DirectAddress ? "；正在对我说话。" : "；未明确叫我，可以旁听或参与。");
                if (environment.SessionType == "group")
                {
                    builder.AppendLine("受众：本群成员；已观察到的账号不代表完整成员或实时在线名单。");
                    if (environment.OwnerPresent) builder.AppendLine("已观察到专属用户在场，但此处仍是公开环境。");
                }
                if (!string.IsNullOrWhiteSpace(environment.ReplyToEventId))
                    builder.AppendLine("这条消息引用了另一条发言；未解析引用内容时，不猜测其作者和指向。");
                if (!environment.AudienceConfirmed) builder.AppendLine("尚未确认谁能收到表达，不能假设是私密交流。");
            }
            var storage = turn.Services.Storage;
            var runtime = SubjectRuntimeLogic.View(turn);
            var life = EnvironmentLogic.IsPublic(turn) ? null : turn.Services.LifeState?.Load(turn.ConversationId);
            // 公开视图不读取旧身体文件中的私人生活内容。
            if (!EnvironmentLogic.IsPublic(turn))
            {
                var location = life?.location ?? MouthLogic.LoadState(turn.Services.DataDirectory).scene;
                if (!string.IsNullOrWhiteSpace(location)) builder.Append("实际位置：").AppendLine(BodySceneValues.Label(location));
                var doing = LifeStateLogic.FormatDoing(life);
                if (doing.Length > 0) builder.Append("正在做：").AppendLine(doing);
            }
            if (!string.IsNullOrWhiteSpace(runtime.OngoingActivity))
                builder.Append("共同文字场景：").AppendLine(runtime.OngoingActivity.Trim());
            builder.Append("状态：").AppendLine(InnerLifeLogic.PresenceLabel(runtime));
            if (!string.IsNullOrWhiteSpace(runtime.Narrative)) builder.Append("上一刻感受：").AppendLine(runtime.Narrative.Trim());
            if (!string.IsNullOrWhiteSpace(runtime.Mood)) builder.Append("情绪：").AppendLine(runtime.Mood.Trim());
            var attention = InnerLifeLogic.LiveAttention(runtime, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (attention.Count > 0) builder.Append("仍在关注：").AppendLine(string.Join("；", attention.Select(x => x.content)));
            var pair = storage.LoadPairIdentity();
            var last = storage.GetRecentDialogueMoments(turn.ConversationId, 2)
                .LastOrDefault(x => x.Id != turn.Moment?.Id && (pair.IsHumanMoment(x.Role) || pair.IsCompanionMoment(x.Role)));
            if (last != null) builder.Append("距离上一段真实相处约")
                .Append(TimeLanguageUtil.ElapsedZh(last.CreatedUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).AppendLine("。");
            if (!EnvironmentLogic.IsPublic(turn))
            {
                var items = storage is TraceSoul2.Manager.SqliteMemoryManager sqlite
                    ? sqlite.GetTodayNewItemsByDay(turn.ConversationId, MemoryDayLogic.CurrentDayKey(DateTimeOffset.Now))
                    : storage.GetTodayNewItems(turn.ConversationId, MemoryDayLogic.CurrentStart(DateTimeOffset.Now).ToUnixTimeMilliseconds(), 20);
                if (items?.Count > 0) builder.AppendLine("今日新识：").AppendLine(string.Join("\n", items.Select(x => "- " + x.Content)));
                var trajectory = DayTrajectoryLogic.Read(storage, turn.ConversationId, MemoryDayLogic.CurrentDayKey(DateTimeOffset.Now));
                if (!string.IsNullOrWhiteSpace(trajectory?.Text)) builder.Append("当天轨迹：").AppendLine(trajectory.Text.Trim());
            }
            return builder.ToString();
        }

        public static string Trigger(TraceTurnContext turn)
        {
            if (EnvironmentLogic.IsHumanInput(turn)) return turn.RequiresExpression
                ? "\n【本轮】对方正在对我说话，需要回应。结合当前环境和关系选择内容与方式。\n"
                : "\n【本轮】听到在场者交谈，可以参与，也可以继续旁听。\n";
            if (HeartbeatLogic.IsHeartbeatContent(turn.Moment?.Content))
                return "\n【本轮】\n" + AgentLoopPrompts.Heartbeat + "\n";
            return "\n【本轮】后台事件唤醒；根据实际变化判断是否需要行动或表达。\n";
        }
    }
}
