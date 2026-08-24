using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;

namespace TraceSoul2.Plugins
{
    /// <summary>出站表达的规范消息结构：与平台无关；平台适配器负责翻译成平台自己的动作。</summary>
    public sealed class TraceOutboundMessageData
    {
        /// <summary>表达类型（TraceOutboundKinds.*）。</summary>
        public string Kind;
        /// <summary>文字内容（Kind=text 时使用）。</summary>
        public string Text;
        /// <summary>图片/语音/文件：本地路径或 URL（Kind=image/voice/file 时使用）。</summary>
        public string File;
        /// <summary>目标会话类型（"group"/"private" 等，由平台适配器翻译）。</summary>
        public string SessionType;
        /// <summary>目标会话 ID（群号或用户号等）。</summary>
        public string SessionId;
    }

    /// <summary>规范表达类型。</summary>
    public static class TraceOutboundKinds
    {
        public const string Text = "text";
        public const string Image = "image";
        public const string Voice = "voice";
        public const string Sticker = "sticker";
        public const string File = "file";
        public const string Action = "action";
    }

    /// <summary>
    /// 平台适配器契约：一个平台 = 一个适配器，负责两类翻译——
    /// 入站：平台原始消息 → 规范 Moment 事件（PluginEventData，role=user，含平台会话信息）；
    /// 出站：规范表达（TraceOutboundMessageData）→ 平台动作，并且必须回传规范的「已发送」事件。
    /// 实际文字进入 Moment；图片、表情、语音等动作回执进入 operational_events。
    /// 平台的连接/鉴权/心跳等传输细节留在平台插件里，适配器只做消息结构翻译。
    /// </summary>
    public interface ITracePlatformAdapter
    {
        /// <summary>适配器所属平台插件 ID。</summary>
        string PlatformId { get; }

        /// <summary>平台原始载荷 → 规范 Moment；不是消息事件（心跳/回包/元事件等）返回 null。</summary>
        PluginEventData ConvertInbound(string platformPayload);

        /// <summary>规范表达 → 平台动作；返回结果必须带 ProducedEvent，供中枢按语义事件/运行回执分流。</summary>
        Task<TraceCapabilityResultData> SendAsync(
            TraceOutboundMessageData message,
            TraceTurnContext context,
            CancellationToken cancellationToken);

        /// <summary>
        /// 平台通用动作（供感官插件调用平台特有接口，如 QQ 空间 get_cookies）：
        /// 返回原始 JSON 响应文本；失败抛异常。
        /// </summary>
        Task<string> CallActionAsync(
            string action,
            System.Collections.Generic.Dictionary<string, object> parameters,
            CancellationToken cancellationToken = default(CancellationToken));
    }
}
