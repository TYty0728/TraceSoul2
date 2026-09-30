using System;
using System.Collections.Generic;

namespace TraceSoul2.Data
{
    /// <summary>平台提供的观察。不能自行声明私密权限；参与者列表允许不完整。</summary>
    public sealed class EnvironmentObservationData
    {
        public string PlatformId { get; init; }
        public string AccountId { get; init; }
        public string SessionId { get; init; }
        public string SessionType { get; init; }
        public string SpeakerId { get; init; }
        public string SpeakerName { get; init; }
        public string ReplyToEventId { get; init; }
        public bool DirectAddress { get; init; }
        public bool ExclusivePair { get; init; }
        public IReadOnlyList<string> ParticipantIds { get; init; } = Array.Empty<string>();
    }

    /// <summary>内核确认的在场快照。与模型状态写集分离，轮内和延迟任务使用同一快照。</summary>
    public sealed class EnvironmentSnapshotData
    {
        public string EnvironmentId { get; init; }
        public string RootConversationId { get; init; }
        public string ContextConversationId { get; init; }
        public string Visibility { get; init; } = "public";
        public bool OwnerPresent { get; init; }
        public bool SpeakerIsOwner { get; init; }
        public bool AudienceConfirmed { get; init; }
        public string PlatformId { get; init; }
        public string AccountId { get; init; }
        public string SessionId { get; init; }
        public string SessionType { get; init; }
        public string SpeakerId { get; init; }
        public string SpeakerName { get; init; }
        public string ReplyToEventId { get; init; }
        public bool DirectAddress { get; init; }
        public IReadOnlyList<string> ParticipantIds { get; init; } = Array.Empty<string>();
        public long UpdatedUnixMs { get; init; }
    }

    public sealed class OwnerAccountBindingData
    {
        public string PlatformId { get; set; }
        public string UserId { get; set; }
    }
}
