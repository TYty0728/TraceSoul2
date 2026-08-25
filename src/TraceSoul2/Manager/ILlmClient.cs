using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;

namespace TraceSoul2.Manager
{
    /// <summary>用于按 BaseUrl 认官网渠道。TimedLlmClient 等包装器必须转发给内层。</summary>
    public interface ILlmEndpoint
    {
        string BaseUrl { get; }
    }

    /// <summary>Host 上的语言模型口。Brain 和插件只看见这个接口，不看见具体厂商。</summary>
    public interface ILlmClient
    {
        string ProviderId { get; }
        string Model { get; }
        /// <summary>
        /// 旧插件 ABI：缓存键加入前的二参数签名。必须长期保留；默认转发到新签名。
        /// 可选参数在 CLR 中仍属于方法签名的一部分，不能直接用三参数方法替换它。
        /// </summary>
        Task<string> CompleteJsonAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken)
        {
            return CompleteJsonAsync(messages, cancellationToken, null);
        }
        Task<string> CompleteJsonAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default(CancellationToken),
            string promptCacheKey = null);
        /// <summary>开口用：普通文本，不强制 JSON 对象。</summary>
        Task<string> CompleteTextAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken)
        {
            return CompleteTextAsync(messages, cancellationToken, null);
        }
        Task<string> CompleteTextAsync(
            List<DeepSeekMessageData> messages,
            CancellationToken cancellationToken = default(CancellationToken),
            string promptCacheKey = null);
        Task<IReadOnlyList<string>> ListModelsAsync(
            CancellationToken cancellationToken = default(CancellationToken));
    }
}
