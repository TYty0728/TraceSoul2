using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TraceSoul2.Plugins
{
    // Platform-neutral expression matching. It never sends, records a Moment or calls an LLM.
    public interface IExpressionLibrary
    {
        ExpressionCatalog Catalog();
        Task<ExpressionMatch> MatchAsync(ExpressionQuery query, CancellationToken token);
        ExpressionImage ReadImage(string id);
    }
    public sealed class EmotionTagScore
    {
        public string tag { get; set; }
        public string category { get; set; }
        public double score { get; set; }
        public double similarity { get; set; }
    }
    public sealed class ExpressionAsset
    {
        public string id { get; set; }
        public string name { get; set; }
        public string[] legacyTags { get; set; }
        public List<EmotionTagScore> tags { get; set; } = new();
        public string[] semanticTags { get; set; } = System.Array.Empty<string>();
        public bool requiresSemanticMatch { get; set; }
    }
    public sealed class ExpressionCandidate
    {
        public string id { get; set; }
        public string name { get; set; }
        public double score { get; set; }
        public double emotionScore { get; set; }
        public double semanticScore { get; set; }
        public bool eligible { get; set; }
        public string reason { get; set; }
    }
    public sealed class SemanticTagScore
    {
        public string tag { get; set; }
        public double score { get; set; }
        public double similarity { get; set; }
        public bool excluded { get; set; }
        public double exclusionScore { get; set; }
    }
    public sealed class ExpressionQuery
    {
        public string text { get; set; } = "";
        public string mood { get; set; } = "";
        public int? topTags { get; set; }
    }
    public sealed class ExpressionMatch
    {
        public string status { get; set; }
        public string reason { get; set; }
        public string selectedId { get; set; }
        public string revision { get; set; }
        public double threshold { get; set; }
        public int topTags { get; set; }
        public List<EmotionTagScore> tags { get; set; } = new();
        public List<ExpressionCandidate> candidates { get; set; } = new();
        public List<SemanticTagScore> semanticTags { get; set; } = new();
        public string semanticMode { get; set; } = "none";
        public double semanticThreshold { get; set; }
    }
    public sealed class ExpressionCatalog
    {
        public string revision { get; set; }
        public string model { get; set; }
        public double threshold { get; set; }
        public int topTags { get; set; }
        public bool profilesReady { get; set; }
        public List<EmotionTagScore> tags { get; set; } = new();
        public List<ExpressionAsset> assets { get; set; } = new();
        public string[] semanticTags { get; set; } = System.Array.Empty<string>();
        public double semanticThreshold { get; set; }
    }
    public sealed class ExpressionImage
    {
        public byte[] bytes { get; set; }
        public string contentType { get; set; }
        // Only the platform adapter uses this validated local asset path. Never expose in dashboard JSON.
        public string path { get; set; }
    }
}
