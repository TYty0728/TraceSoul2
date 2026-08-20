using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;

namespace TraceSoul2.Manager
{
    /// <summary>Host 上的语言模型口。Brain 和插件只看见这个接口，不看见具体厂商。</summary>
    public interface ILlmClient
    {
        string ProviderId { get; }
        string Model { get; }
        Task<string> CompleteJsonAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default(CancellationToken));
        Task<IReadOnlyList<string>> ListModelsAsync(
            CancellationToken cancellationToken = default(CancellationToken));
    }
}
