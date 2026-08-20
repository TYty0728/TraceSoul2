using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.Tools.Memory
{
    /// <summary>
    /// 语义向量拼装引擎（Host 注入到记忆神经）：
    /// query 用 BGE 编码后，在候选条目（已由子代理定位收窄）里做余弦 top-k。
    /// 条目向量在日构筑管线预计算，存放在 vectors 库的 event_entry_vectors 表。
    /// </summary>
    public sealed class MemoryRecallEngine : IMemoryRecallEngine
    {
        private readonly OnnxBgeEncoder encoder;
        private readonly SqliteVectorManager vectors;

        public MemoryRecallEngine(OnnxBgeEncoder encoder, SqliteVectorManager vectors)
        {
            this.encoder = encoder ?? throw new ArgumentNullException("encoder");
            this.vectors = vectors ?? throw new ArgumentNullException("vectors");
        }

        public bool IsAvailable { get { return encoder != null && vectors != null; } }
        public string ModelId { get { return encoder.ModelId; } }
        public int DefaultTopK { get; set; } = 3;

        public List<MemoryRecallHit> Search(string query, IReadOnlyList<string> candidateEntryIds, int topK)
        {
            if (string.IsNullOrWhiteSpace(query) || candidateEntryIds == null || candidateEntryIds.Count == 0)
                return new List<MemoryRecallHit>();
            if (topK <= 0) topK = 3;

            var stored = vectors.GetEntryEmbeddings(candidateEntryIds, encoder.ModelId);
            if (stored.Count == 0) return new List<MemoryRecallHit>();

            var queryVector = encoder.Encode(query, VectorTextPurpose.Query);
            var hits = new List<MemoryRecallHit>(stored.Count);
            foreach (var pair in stored)
            {
                var score = Cosine(queryVector, pair.Value);
                hits.Add(new MemoryRecallHit { EntryId = pair.Key, Score = score });
            }
            return hits.OrderByDescending(x => x.Score).Take(topK).ToList();
        }

        public void PutEntryVector(string entryId, string summaryText)
        {
            var text = (summaryText ?? string.Empty).Trim();
            if (text.Length == 0 || string.IsNullOrWhiteSpace(entryId)) return;
            var hash = EntryEmbedder.Hash(text + "|" + encoder.ModelId);
            if (vectors.HasEntryEmbedding(entryId, encoder.ModelId, hash)) return;
            var vector = encoder.Encode(text, VectorTextPurpose.Index);
            vectors.PutEntryEmbedding(entryId, encoder.ModelId, hash, vector);
        }

        private static float Cosine(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length == 0) return 0f;
            var dot = 0f;
            for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
            return dot; // 两侧都做过 L2 归一化，点积即余弦。
        }
    }
}
