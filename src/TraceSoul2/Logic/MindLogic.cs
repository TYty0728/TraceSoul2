using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.Logic
{
    /// <summary>Agent 状态归一化及历史记录兼容工具；不提供独立模型决策入口。</summary>
    public static class MindLogic
    {
        public static MindDecisionData Normalize(MindDecisionData output, bool alreadyLeft = false)
        {
            output = output ?? new MindDecisionData();
            output.beat = output.BeatValue();
            if (alreadyLeft && output.BeatValue() == MindBeatValues.Leave)
            {
                output.beat = MindBeatValues.Now;
                output.leave = string.Empty;
            }
            output.tags = string.Join("、", output.ParseTags());
            output.query = Limit((output.query ?? string.Empty).Trim(), 80);
            output.mood = OneLine(output.mood);
            if (output is AgentStepData agent) output.mood_changed = agent.HasStateField("mood") && output.mood.Length > 0;
            output.new_fact = (output.new_fact ?? string.Empty).Trim();
            output.leave = Limit((output.leave ?? string.Empty).Trim(), 80);
            output.note = (output.note ?? string.Empty).Trim();
            output.today = (output.today ?? string.Empty).Trim();
            output.inner = OneLine(output.inner);
            output.scene = Limit(OneLine(output.scene), 160);
            output.speak_center = Limit(OneLine(output.speak_center), 100);
            // 长期认知只允许日终复盘产出；白天字段保留仅为旧模型 JSON 兼容。
            output.cognition = string.Empty;
            output.archive = false;
            output.heartbeat_intent = Limit(OneLine(output.heartbeat_intent), 120);
            output.next_heartbeat_plan = Limit(OneLine(output.next_heartbeat_plan), 120);
            if (output.ClearsAttention())
                output.attention = "无";
            else
            {
                var held = output.ParseAttention().Select(x => Limit(x, 80)).Where(x => x.Length > 0).ToList();
                output.attention = held.Count == 0 ? string.Empty : string.Join("、", held);
            }
            if (LooksLikeBareDate(output.today)) output.today = string.Empty;
            if (output.BeatValue() != MindBeatValues.Leave) output.leave = string.Empty;
            if (output.BeatValue() == MindBeatValues.Leave)
            {
                output.tags = string.Empty;
                output.query = string.Empty;
            }
            output.next_heartbeat_minutes = HeartbeatLogic.ClampMinutes(output.next_heartbeat_minutes);
            if (output.sleep) output.next_heartbeat_minutes = 0;
            output.tool_call = Limit((output.tool_call ?? string.Empty).Trim(), 80);
            output.tool_input = Limit((output.tool_input ?? string.Empty).Trim(), 500);
            if (output.tool_call.Length == 0) output.tool_input = string.Empty;
            output.sticker = output.StickerValue();
            output.image = output.ImageValue();
            output.location = output.LocationValue();
            output.activity = OneLine(output.activity);
            output.activity_detail = OneLine(output.activity_detail);
            // 表情是否发送不再由心智卡决定；这里保留旧字段兼容，但统一归零。
            output.sticker = MindAtmosphereValues.None;
            if (output.sleep || output.BeatValue() == MindBeatValues.Leave)
            {
                output.image = MindAtmosphereValues.None;
                output.sticker = MindAtmosphereValues.None;
            }
            if (output.WantsImage()) output.sticker = MindAtmosphereValues.None;
            return output;
        }

        /// <summary>旧记录历史视图的纯组装工具，不是模型执行入口。</summary>
        internal static List<DeepSeekMessageData> AssembleTurnMessages(
            string systemPrompt,
            TraceTurnContext turn,
            string currentUserContent)
        {
            var messages = new List<DeepSeekMessageData>
            {
                new DeepSeekMessageData("system", systemPrompt ?? string.Empty)
            };
            messages.AddRange(BuildRecentChatHistory(turn));
            if (!HeartbeatLogic.IsHeartbeatContent(currentUserContent))
                messages.Add(new DeepSeekMessageData("user", currentUserContent ?? string.Empty));
            return messages;
        }

        /// <summary>兼容入口复用公共历史视图，保留每条发言的时间与边界。</summary>
        internal static List<DeepSeekMessageData> BuildRecentChatHistory(TraceTurnContext turn)
        {
            return CommonContextPackLogic.BuildRecentChatHistory(turn);
        }

        /// <summary>出站入库的系统占位，不是对她说的话，不能进对话历史。</summary>
        internal static bool IsOutboundProtocolMoment(string content)
        {
            var text = (content ?? string.Empty).Trim();
            if (text.Length < 4 || text[0] != '[') return false;
            return text.StartsWith("[QQ ", StringComparison.Ordinal) ||
                   text.StartsWith("[CQ:", StringComparison.OrdinalIgnoreCase);
        }

        private static string OneLine(string value)
        {
            return string.Join(" ", (value ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static bool LooksLikeBareDate(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) return true;
            if (value.IndexOf('。') >= 0 || value.IndexOf('，') >= 0 || value.IndexOf(',') >= 0)
                return false;
            return value.IndexOf("年", StringComparison.Ordinal) >= 0 &&
                   value.IndexOf("月", StringComparison.Ordinal) >= 0 &&
                   value.IndexOf("日", StringComparison.Ordinal) >= 0 &&
                   value.Length <= 14;
        }

        /// <summary>原指令已经写明篇幅。超出时让同一次生成重写，不在这里截断或清空。</summary>
        public static string ProseLengthError(MindDecisionData output)
        {
            if (output == null) return null;
            string Over(string path, string value, int max)
            {
                var text = (value ?? string.Empty).Trim();
                if (text.Length <= max) return null;
                return path + " 当前" + text.Length + "字，最多" + max + "字。按原篇幅重新写下。上一份太长，不要交回上一份。";
            }
            return Over("$.today", output.today, 60)
                ?? Over("$.new_fact", output.new_fact, TodayNewItemRecord.MaxContentChars)
                ?? Over("$.inner", output.inner, 120)
                ?? Over("$.activity", output.activity, 80)
                ?? Over("$.activity_detail", output.activity_detail, 160);
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }

    }
}
