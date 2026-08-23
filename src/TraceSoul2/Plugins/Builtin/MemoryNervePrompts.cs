using System.Text;
using TraceSoul2.Data;

namespace TraceSoul2.Plugins.Builtin
{
    /// <summary>记忆插件的子代理提示词与回忆注入片段。</summary>
    public static class MemoryNervePrompts
    {
        public const string TodayNewHeader = "今天刚知道的：";
        public const string ArchiveUserPrefix = "刚结束的话题对话记录：\n";
        public const string RouteUserPrefix = "需要定位的回忆：";
        public const string MissingArchiveSummary = "归档总结输出缺少 summary。";
        public const string MissingRoute = "记忆神经子代理没有给出有效定位。";
        public const string AwakenedHeader = "此刻想起的共同记忆：";
        public const string EmptyHits = "（候选范围内没有足够相近的细节。）";
        public const string CognitionHeader = "相关认知（我的第一人称理解）：";
        public const string EmptyList = "（空）";
        public const string CrossConditionsHeader = "可选交叉条件（只能原样复制）：";
        public const string TimeLabelsPrefix = "时间时段：";
        public const string MonthBucketsPrefix = "日期桶(yyyy-MM)：";
        public const string PlacePrefix = "地点：";
        public const string PersonPrefix = "人物：";
        public const string MoodPrefix = "心情：";
        public const string ArchiveWhenToUse =
            "话题明显转变、上一个话题或事件已经结束时，把刚才这段对话收成一条可以以后想起的事；对方明确说『记一下』『帮我记住』时也调用。";
        public const string ArchiveWhenNotToUse = "话题还在进行中、只有零散寒暄、没有成块内容时。";
        public const string ActivateWhenToUse =
            "对方问你是否记得、问起一起经历过的具体事情，或你回答前需要共同记忆佐证。先 activate，再 finish。";
        public const string ActivateWhenNotToUse =
            "天气、问候、嗯嗯哈哈，或当前对话原文已经足够回答、不需要翻找过往经历时。";

        public static string ArchiveSystem(PairIdentity pair)
        {
            pair = pair ?? PairIdentity.Missing;
            var builder = new StringBuilder();
            builder.AppendLine(pair.Apply("你是 {assname} 的记忆整理助手，不是他自己。下面是一段刚结束的话题的对话记录（来自若干条 Moment：Moment＝一次进入意识的原始记录）。请："));
            builder.AppendLine("1. summary：一句客观、事实化的话题总结（谁做了什么/发生了什么，不超过80字）；");
            builder.AppendLine(pair.Apply("2. detail：以 {assname} 的第一人称记下一小段确实发生过的经历；“我”只指 {assname}，“你”只指 {username}。保留这段经历里真正有意义的动作、说法和私人意象，写清楚发生了什么，以及它在我心里留下了什么。没有值得记下的细节就留空；"));
            builder.AppendLine(pair.Apply("3. mood：{username}在这段对话里的心情词（如 轻松、开心、平静、难过），读不出就留空。"));
            builder.AppendLine("只输出 JSON：{\"summary\":\"一句总结\",\"detail\":\"第一人称细节\",\"mood\":\"心情词\"}");
            return builder.ToString();
        }

        public const string RouteRules = @"你是记忆定位器，只负责圈定检索范围，不回答对方、不写回记忆，也不解释数据库结构。你不是同伴本人。
内部的域与维度关系已经由存储层维护；你只选择可读概念和明确提到的交叉条件。
Moment 是一次进入意识的原始记录；本任务处理的是对某次回忆的定位请求，不是那条 Moment 本身。

规则：
1. has_memory=false 只用于问题明显描述从未经历的事；措辞不同或概念名不完全一致时，仍应先选最接近范围。
2. concept_labels 选 0-3 个最贴切的概念名，必须从下方原样复制；不要输出任何内部 ID。
3. 时段、地点、人物、心情只在问题明确提及时选择；年份月份或『第一次/最初/去年/某月』放 month_buckets。
4. 指向开端时选择最早日期桶，并结合命名、形象、身份等初识概念；refined_query 改写成 10-30 字事实检索句。

只输出 JSON：
{""has_memory"":true,""concept_labels"":[],""time_labels"":[],""month_buckets"":[],""place_labels"":[],""person_labels"":[],""mood_labels"":[],""refined_query"":"""",""reason"":""""}";

        public static string OptionalConceptsHeader(bool truncated, int shown, int hidden)
        {
            return "可选概念名" +
                   (truncated ? "（按活跃度选取 " + shown + " 个，其余 " + hidden + " 个本次未列出）" : string.Empty) +
                   "：";
        }

        public static string IndexCountFooter(int totalIndexes)
        {
            return "（当前共 " + totalIndexes + " 条事件索引。）";
        }

        public static string RoutedSlices(string concepts, int totalEntries, int shown)
        {
            return "按这些主题想起的共同经历：" + concepts +
                   "。（共 " + totalEntries + " 条，这里取 " + shown + " 条，★＝对得上主题）";
        }

        public static string UnroutedSlices(bool strong, int totalEntries, int shown)
        {
            return strong
                ? "没有对上主题，已在全部共同经历里找最相关的（共 " + totalEntries + " 条，取 " + shown + " 条）："
                : "没有特别贴近的，以下是时间最近的共同经历（共 " + totalEntries + " 条，取最近 " + shown + " 条）：";
        }
    }
}
