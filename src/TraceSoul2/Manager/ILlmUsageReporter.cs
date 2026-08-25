using TraceSoul2.Logic;

namespace TraceSoul2.Manager
{
    /// <summary>可选口：这次请求的 token / 缓存用量。不是 PluginApi 契约，Host 时序日志用。</summary>
    public interface ILlmUsageReporter
    {
        LlmUsageData LastUsage { get; }
    }
}
