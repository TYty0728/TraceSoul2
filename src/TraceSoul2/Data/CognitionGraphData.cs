using System;
using System.Collections.Generic;
using System.Linq;

namespace TraceSoul2.Data
{
    /// <summary>四个理解领域可以重叠；未分类旧数据不自动猜测归属。</summary>
    public static class PuzzleDomains
    {
        public static string Pack(IEnumerable<string> domains) => string.Join(",",
            (domains ?? Array.Empty<string>()).Where(LifeRouteValues.IsDomain)
            .Select(x => x.ToLowerInvariant()).Distinct().OrderBy(x => x, StringComparer.Ordinal));
        public static string Label(string domains) => string.Join("、", (domains ?? "").Split(',')
            .Select(x => x == "user" ? "他" : x == "world" ? "世界" : x == "ass" ? "我" : x == "relation" ? "关系" : "待分类"));
        public static bool Live(string status) => status == "active" || status == "weakened";
    }
}

namespace TraceSoul2.Manager
{
    using TraceSoul2.Data;
    /// <summary>可选的认知图读取接口，既有 IMemoryStore 插件无需增加实现。</summary>
    public interface ICognitionGraphStore
    {
        List<CognitionSliceRecord> GetCognitionNodes(int take);
        List<CognitionEdgeRecord> GetCognitionEdges(IEnumerable<string> nodeIds);
        List<CognitionEvidenceRecord> GetCognitionEvidence(IEnumerable<string> nodeIds);
        List<MomentRecord> GetEvidenceMoments(IEnumerable<string> momentIds);
    }
}
