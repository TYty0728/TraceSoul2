using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Prompts;
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
                CorePrompts.MindTemplates.RecallInstruction,
                CorePrompts.MindTemplates.RecallSense,
                CorePrompts.MindTemplates.RecallExamples),
            T("perform", "当场做完",
                CorePrompts.MindTemplates.PerformInstruction,
                CorePrompts.MindTemplates.PerformSense,
                CorePrompts.MindTemplates.PerformExamples),
            T("choose", "短商量",
                CorePrompts.MindTemplates.ChooseInstruction,
                CorePrompts.MindTemplates.ChooseSense,
                CorePrompts.MindTemplates.ChooseExamples),
            T("hold", "接住",
                CorePrompts.MindTemplates.HoldInstruction,
                CorePrompts.MindTemplates.HoldSense,
                CorePrompts.MindTemplates.HoldExamples),
            T("leave", "出门",
                CorePrompts.MindTemplates.LeaveInstruction,
                CorePrompts.MindTemplates.LeaveSense,
                CorePrompts.MindTemplates.LeaveExamples),
            T("note", "记下",
                CorePrompts.MindTemplates.NoteInstruction,
                CorePrompts.MindTemplates.NoteSense,
                CorePrompts.MindTemplates.NoteExamples),
            T("release", "放下",
                CorePrompts.MindTemplates.ReleaseInstruction,
                CorePrompts.MindTemplates.ReleaseSense,
                CorePrompts.MindTemplates.ReleaseExamples)
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
            builder.AppendLine(CorePrompts.MindTemplates.OrganizedHeader);
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
            string id, string label, string instruction, string sense, IEnumerable<string> examples)
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
