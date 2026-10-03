using System;
using System.Globalization;
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
            => State(turn, DateTimeOffset.Now);

        internal static string State(TraceTurnContext turn, DateTimeOffset now)
        {
            var builder = new StringBuilder("【当前状态】\n【此刻】\n");
            var time = turn.Workspace.ContextBlocks.FirstOrDefault(x => x?.FacetId == "time.context")?.Content;
            builder.AppendLine(string.IsNullOrWhiteSpace(time) ? CurrentTime(now) : time.Trim());
            var environment = turn.Environment;
            if (environment != null)
            {
                builder.AppendLine(environment.Visibility == "private"
                    ? "环境：私密。已确认只有我与专属用户单独相处。"
                    : "环境：公开。按本环境可见的经历与关系交流。");
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
                    builder.AppendLine("这条消息引用了另一条发言，具体内容以解析出的引用为准。");
                if (!environment.AudienceConfirmed) builder.AppendLine("接收表达的受众尚待确认，目前按公开范围交流。");
            }
            var storage = turn.Services.Storage;
            var runtime = SubjectRuntimeLogic.View(turn);
            builder.Append("状态：").AppendLine(InnerLifeLogic.PresenceLabel(runtime));
            var pair = storage.LoadPairIdentity();
            var last = storage.GetRecentDialogueMoments(turn.ConversationId, 2)
                .LastOrDefault(x => x.Id != turn.Moment?.Id && (pair.IsHumanMoment(x.Role) || pair.IsCompanionMoment(x.Role)));
            if (last != null) builder.Append("上次交流：").Append(Timestamp(last.CreatedUnixMs)).AppendLine("（北京时间）。");

            var carried = new StringBuilder();
            var life = EnvironmentLogic.IsPublic(turn) ? null : turn.Services.LifeState?.Load(turn.ConversationId);
            // 公开视图不读取旧身体文件中的私人生活内容。
            if (!EnvironmentLogic.IsPublic(turn))
            {
                var location = life?.location ?? MouthLogic.LoadState(turn.Services.DataDirectory).scene;
                if (!string.IsNullOrWhiteSpace(location))
                    Record(carried, "生活位置", BodySceneValues.Label(location), life?.location_updated_unix_ms ?? 0,
                        life == null ? "沿用的身体场景" : LifeSource(life.location_source, life.location_updated_unix_ms));
                var doing = LifeStateLogic.FormatDoing(life);
                if (doing.Length > 0) Record(carried, "生活活动", doing, life.activity_updated_unix_ms,
                    LifeSource(life.activity_source, life.activity_updated_unix_ms));
            }
            Record(carried, "共同文字场景", runtime.OngoingActivity, 0, "本环境留下的场景");
            // 只用持久主体片段自己的时间；旧 runtime 的整体更新时间不能代表每个字段。
            var subject = turn.Environment == null ? null : PuzzleViewLogic.Read<SubjectState>(
                storage.LoadPluginDocument(SubjectRuntimeLogic.StoreId, PuzzleViewLogic.Root(turn)));
            long FragmentTime(SubjectFragment fragment, string text) => fragment != null && fragment.Text == text &&
                (!EnvironmentLogic.IsPublic(turn) || fragment.Context == turn.ConversationId) ? fragment.UpdatedUnixMs : 0;
            Record(carried, "留下的感受", runtime.Narrative, FragmentTime(subject?.Narrative, runtime.Narrative), "内心记录");
            Record(carried, "延续的情绪", runtime.Mood, FragmentTime(subject?.Mood, runtime.Mood), "情绪记录");
            foreach (var item in InnerLifeLogic.LiveAttention(runtime, now.ToUnixTimeMilliseconds()))
                Record(carried, "仍在关注", item.content, item.UpdatedUnixMs, "关注记录");
            if (carried.Length > 0)
                builder.AppendLine("\n【延续下来的生活与心绪】")
                    .AppendLine("我从上一次留下的生活与心绪接着往前。此刻的时间、在场的人和新的消息，帮助我感受这一刻有什么变化。")
                    .AppendLine("以下记录时间均为北京时间。").Append(carried);
            if (!EnvironmentLogic.IsPublic(turn))
            {
                var day = MemoryDayLogic.CurrentDayKey(now);
                var daily = new StringBuilder();
                var items = storage is TraceSoul2.Manager.SqliteMemoryManager sqlite
                    ? sqlite.GetTodayNewItemsByDay(turn.ConversationId, day)
                    : storage.GetTodayNewItems(turn.ConversationId, MemoryDayLogic.CurrentStart(now).ToUnixTimeMilliseconds(), 20);
                if (storage is TraceSoul2.Manager.SqliteMemoryManager timeline)
                {
                    var entries = DayTrajectoryOverviewLogic.Read(timeline, turn.ConversationId, day);
                    if (entries.Count > 0) daily.AppendLine("今日事件与新进展：");
                    foreach (var entry in entries)
                        daily.AppendLine(DayTrajectoryLogic.OverviewLine(entry, now));
                }
                else
                {
                    var trajectory = DayTrajectoryLogic.Read(storage, turn.ConversationId, day);
                    if (!string.IsNullOrWhiteSpace(trajectory?.Text))
                        daily.AppendLine("经历记录（原有摘要）：").AppendLine(trajectory.Text.Trim());
                }
                if (items?.Count > 0)
                {
                    daily.AppendLine("新得知的事：");
                    foreach (var item in items) daily.Append('[').Append(Timestamp(item.CreatedUnixMs)).Append("] ").AppendLine(item.Content);
                }
                if (daily.Length > 0)
                {
                    var start = MemoryDayLogic.CurrentStart(now);
                    builder.AppendLine("\n【这一天的经历】").Append("日期范围：")
                        .Append(start.ToString("yyyy年M月d日 HH:mm", CultureInfo.InvariantCulture)).Append(" 至 ")
                        .Append(start.AddDays(1).ToString("yyyy年M月d日 HH:mm", CultureInfo.InvariantCulture))
                        .AppendLine("（北京时间，结束时刻归下一日）。").Append(daily);
                }
            }
            return builder.ToString();
        }

        internal static string CurrentTime(DateTimeOffset now)
        {
            var local = now.ToOffset(MemoryDayLogic.ChinaOffset);
            return "现在是 " + local.ToString("yyyy年M月d日 HH:mm", CultureInfo.InvariantCulture) +
                "（" + TimeLanguageUtil.WeekZh(local) + "，北京时间 UTC+08:00）。";
        }

        private static string Timestamp(long unixMs)
        {
            if (unixMs <= 0) return "时间未记录";
            try { return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToOffset(MemoryDayLogic.ChinaOffset)
                    .ToString("yyyy年M月d日 HH:mm", CultureInfo.InvariantCulture); }
            catch (ArgumentOutOfRangeException) { return "时间未记录"; }
        }

        private static void Record(StringBuilder builder, string label, string text, long time, string source)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            builder.Append(label).Append("（").Append(source).Append("；").Append(Timestamp(time))
                .Append("）：").AppendLine(text.Trim());
        }

        private static string LifeSource(string source, long time) => source switch
        {
            LifeStateSourceValues.User => "本人设置",
            LifeStateSourceValues.Sensor => "设备观察",
            LifeStateSourceValues.Plugin => "插件记录",
            LifeStateSourceValues.Mind => "自己留下的生活记录",
            _ => time > 0 ? "系统记录" : "初始设定"
        };

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
