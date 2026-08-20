using System.Collections.Generic;

namespace TraceSoul2.Plugins
{
    /// <summary>语义向量召回的一命中项：条目 id + 余弦相似度。</summary>
    public sealed class MemoryRecallHit
    {
        public string EntryId { get; set; }
        public float Score { get; set; }
    }

    /// <summary>
    /// 记忆神经的语义拼装引擎（由宿主注入）：把 query 编码为向量，
    /// 在给定的候选条目范围内取余弦最相近的 top-k 条细节。
    /// Unity 侧可用 Sentis/BGE 实现；Host 侧用 ONNX Runtime 跑同一模型。
    /// </summary>
    public interface IMemoryRecallEngine
    {
        bool IsAvailable { get; }
        string ModelId { get; }

        /// <summary>Brain 没传 top_k 时使用的默认拼装条数（控制台可配）。</summary>
        int DefaultTopK { get; set; }

        /// <summary>候选为空时返回空列表；candidateEntryIds 数量应已被路由定位收窄。</summary>
        List<MemoryRecallHit> Search(string query, IReadOnlyList<string> candidateEntryIds, int topK);

        /// <summary>实时归档时给新条目的一句话总结编码入库，供以后召回。</summary>
        void PutEntryVector(string entryId, string summaryText);
    }
}
