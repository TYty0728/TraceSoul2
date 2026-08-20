using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Tools.Memory;

namespace TraceSoul2.Host
{
    /// <summary>宿主注入给插件的文本语义向量服务：ONNX BGE 编码（CPU）。</summary>
    public sealed class BgeEmbeddingService : IEmbeddingService
    {
        private readonly OnnxBgeEncoder encoder;

        public BgeEmbeddingService(OnnxBgeEncoder encoder)
        {
            this.encoder = encoder;
        }

        public string ModelId { get { return encoder == null ? string.Empty : encoder.ModelId; } }

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.Run(
                () => encoder.Encode(text ?? string.Empty, VectorTextPurpose.Query),
                cancellationToken);
        }
    }
}
