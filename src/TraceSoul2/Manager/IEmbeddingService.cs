using System.Threading;
using System.Threading.Tasks;

namespace TraceSoul2.Manager
{
    /// <summary>
    /// 文本语义向量编码服务（宿主注入，如 Host 侧 ONNX BGE）：外部插件用它做语义匹配
    /// （表情包相似度、短文本聚类等），不需要自己带模型。
    /// </summary>
    public interface IEmbeddingService
    {
        string ModelId { get; }
        Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default(CancellationToken));
    }
}
