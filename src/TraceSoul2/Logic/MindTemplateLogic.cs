using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 心智的情境模版：宪法只保留填卡协议，具体怎么组织由这一句 Moment 向量预选。
    /// 命中才注入动态段；打不中就保持通用心智。
    /// </summary>
    public static class MindTemplateLogic
    {
        public const int CandidateCap = 2;
        public const float MinScore = 0.22f;
        public const float WindowFromBest = 0.16f;

        private static readonly ConcurrentDictionary<string, float[]> TemplateVectors =
            new ConcurrentDictionary<string, float[]>(StringComparer.Ordinal);

        public static readonly IReadOnlyList<MindTemplate> All = new[]
        {
            T("recall", "翻旧事",
                "记不记得、当时、那一次、第一次、我们去过：这一拍要翻那些共同经历，从下面标签里勾对得上的。",
                "你还记得吗。还记得那一次吗。还记得当时吗。当时我们。第一次见面。那晚后来。那时候你。我们去过吗。上次我们一起。从前那件事。",
                "你还记得吗", "还记得那一次吗", "还记得当时吗", "还记得上次吗", "还记得上次情人节吗",
                "我们第一次见面", "那晚后来", "那时候你", "我们去过吗", "上次我们一起"),
            T("perform", "当场做完",
                "讲、唱、念、演：这一拍把内容做完，不要只答应。故事可以是新的。",
                "给我讲个故事。讲个故事嘛。再讲一个。唱首歌给我听。念给我听。演一段。来一段。编一个故事。",
                "给我讲个故事", "讲个故事嘛", "我又想听你讲故事了", "再讲一个",
                "唱首歌给我听", "念给我听", "演一段给我看", "来一段", "编一个故事"),
            T("choose", "短商量",
                "吃什么、选哪个、要不要、还是：这一拍要短，给两三个选项或把选择权递给她。对得上的口味、习惯可以勾。",
                "中午吃什么呀。我们吃什么。晚上吃什么。要不要点外卖。这个还是那个。选哪个。穿哪件。点什么。",
                "中午吃什么呀", "我们吃什么", "晚上吃什么", "要不要点外卖",
                "这个还是那个", "选哪个", "穿哪件", "点什么"),
            T("hold", "接住",
                "靠着、抱、陪着、只要我在：这一拍接住身体和语气，不要总结成心情说明，也不要翻成旧事。",
                "我靠着你。抱我。陪我待一会儿。亲亲我。挨着我。在我旁边。想挨着你。",
                "我靠着你", "抱我", "陪我待一会儿", "亲亲我", "挨着我", "在我旁边"),
            T("leave", "出门",
                "去查、去搜、帮我看看、会等很久：这一拍先出门办事，开口只要先说等一下。",
                "帮我查一下。你搜搜这个。上网看看。帮我查查。帮我搜一下。等一下帮我看看。",
                "帮我查一下", "你搜搜这个", "上网看看", "帮我查查", "帮我搜一下", "等一下帮我看看"),
            T("note", "记下",
                "帮我记着、你记住、以后别忘了：把她要记下的那一句写下来。",
                "帮我记一下。帮我记着。你记住这句话。以后别忘了。记到心里。",
                "帮我记一下", "帮我记着", "你记住这句话", "以后别忘了"),
            T("release", "放下",
                "讲完了、告一段落、心里安静了：这一拍从手上拿开。手里没了写「无」。这段过完就 review=true。",
                "讲完了。故事讲完了。这段过了。告一段落。心里安静了。就这样吧。先这样。",
                "讲完了", "故事讲完了", "这段过了", "告一段落", "心里安静了", "就这样吧", "先这样")
        };

        public static async Task<List<MindTemplate>> SelectAsync(
            string query,
            IEmbeddingService embedding,
            int cap,
            CancellationToken cancellationToken)
        {
            if (embedding == null)
                return Select(query, new BagOfCharsVectorEncoder(), cap);
            if (string.IsNullOrWhiteSpace(query)) return new List<MindTemplate>();
            var queryVector = await embedding.EmbedAsync(query, cancellationToken);
            VectorMathUtil.NormalizeInPlace(queryVector);
            var scored = new List<KeyValuePair<MindTemplate, float>>();
            foreach (var item in All)
            {
                var senseVector = await EmbedTextAsync(embedding, item.Sense, cancellationToken);
                var best = VectorMathUtil.Cosine(queryVector, senseVector);
                foreach (var prototype in item.Examples)
                {
                    var vector = await EmbedTextAsync(embedding, prototype, cancellationToken);
                    best = Math.Max(best, Blend(query, prototype, VectorMathUtil.Cosine(queryVector, vector)));
                }
                scored.Add(new KeyValuePair<MindTemplate, float>(item, best));
            }
            return Take(scored, cap);
        }

        public static List<MindTemplate> Select(string query, IVectorEncoder encoder, int cap)
        {
            if (encoder == null || string.IsNullOrWhiteSpace(query))
                return new List<MindTemplate>();
            var queryVector = encoder.Encode(query, VectorTextPurpose.Query);
            VectorMathUtil.NormalizeInPlace(queryVector);
            var scored = All.Select(item =>
            {
                var senseVector = encoder.Encode(item.Sense, VectorTextPurpose.Index);
                VectorMathUtil.NormalizeInPlace(senseVector);
                var best = VectorMathUtil.Cosine(queryVector, senseVector);
                foreach (var prototype in item.Examples)
                {
                    var vector = encoder.Encode(prototype, VectorTextPurpose.Index);
                    VectorMathUtil.NormalizeInPlace(vector);
                    best = Math.Max(best, Blend(query, prototype, VectorMathUtil.Cosine(queryVector, vector)));
                }
                return new KeyValuePair<MindTemplate, float>(item, best);
            }).ToList();
            return Take(scored, cap);
        }

        public static string Format(IEnumerable<MindTemplate> templates)
        {
            var list = (templates ?? Enumerable.Empty<MindTemplate>()).Where(x => x != null).ToList();
            if (list.Count == 0) return string.Empty;
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("【这一拍的组织】");
            foreach (var item in list)
                builder.AppendLine("- " + item.Instruction);
            return builder.ToString().TrimEnd();
        }

        private static async Task<float[]> EmbedTextAsync(
            IEmbeddingService embedding,
            string text,
            CancellationToken cancellationToken)
        {
            var key = embedding.ModelId + "\n" + text;
            float[] cached;
            if (TemplateVectors.TryGetValue(key, out cached)) return cached;
            var vector = await embedding.EmbedAsync(text, cancellationToken);
            VectorMathUtil.NormalizeInPlace(vector);
            return TemplateVectors.GetOrAdd(key, vector);
        }

        private static float Blend(string query, string prototype, float cosine)
        {
            query = query ?? string.Empty;
            prototype = prototype ?? string.Empty;
            if (query.Length == 0 || prototype.Length == 0) return cosine;
            if (query.IndexOf(prototype, StringComparison.Ordinal) >= 0 ||
                prototype.IndexOf(query, StringComparison.Ordinal) >= 0)
                return Math.Max(cosine, 0.99f);
            return cosine;
        }

        private static List<MindTemplate> Take(List<KeyValuePair<MindTemplate, float>> scored, int cap)
        {
            cap = Math.Max(1, Math.Min(CandidateCap, cap));
            var ordered = (scored ?? new List<KeyValuePair<MindTemplate, float>>())
                .OrderByDescending(x => x.Value)
                .ToList();
            if (ordered.Count == 0 || ordered[0].Value < MinScore)
                return new List<MindTemplate>();
            var floor = Math.Max(MinScore, ordered[0].Value - WindowFromBest);
            return ordered
                .Where(x => x.Value + 0.0001f >= floor)
                .Take(cap)
                .Select(x => x.Key)
                .ToList();
        }

        private static MindTemplate T(
            string id, string label, string instruction, string sense, params string[] examples)
        {
            return new MindTemplate(id, label, instruction, sense, examples);
        }
    }

    public sealed class MindTemplate
    {
        public string Id { get; private set; }
        public string Label { get; private set; }
        public string Instruction { get; private set; }
        public string Sense { get; private set; }
        public IReadOnlyList<string> Examples { get; private set; }

        public MindTemplate(
            string id, string label, string instruction, string sense, IEnumerable<string> examples)
        {
            Id = id ?? string.Empty;
            Label = label ?? string.Empty;
            Instruction = instruction ?? string.Empty;
            Sense = (sense ?? string.Empty).Trim();
            Examples = (examples ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        }
    }
}
