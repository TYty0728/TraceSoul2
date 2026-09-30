using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    public sealed class EnvironmentSettings
    {
        public List<OwnerAccountBindingData> OwnerAccounts { get; set; } = new();
        /// <summary>由本人确认可公开的自我介绍；旧身份卡不自动视为可公开。</summary>
        public string PublicIdentity { get; set; } = "";
    }

    /// <summary>身份、受众与上下文归属的确定性边界。平台只报观察，模型不能写权限。</summary>
    public static class EnvironmentLogic
    {
        private const string StoreId = "kernel.environment";
        public static EnvironmentSettings Settings(IMemoryStore store)
        {
            var settings = Read<EnvironmentSettings>(store.LoadPluginDocument(StoreId, "settings")) ?? new();
            settings.OwnerAccounts = (settings.OwnerAccounts ?? new()).Where(x => x != null &&
                !string.IsNullOrWhiteSpace(x.PlatformId) && !string.IsNullOrWhiteSpace(x.UserId)).ToList();
            return settings;
        }

        public static void SaveSettings(IMemoryStore store, EnvironmentSettings settings)
        {
            if (settings == null || settings.OwnerAccounts == null || settings.OwnerAccounts.Count > 32 ||
                settings.OwnerAccounts.Any(x => x == null || string.IsNullOrWhiteSpace(x.PlatformId) ||
                    string.IsNullOrWhiteSpace(x.UserId) || x.PlatformId.Length > 100 || x.UserId.Length > 160) ||
                (settings.PublicIdentity?.Length ?? 0) > 2000)
                throw new ArgumentException("请填写有效的平台和本人账号；最多32项，公开介绍最多2000字。");
            foreach (var item in settings.OwnerAccounts)
            { item.PlatformId = item.PlatformId.Trim(); item.UserId = item.UserId.Trim(); }
            store.SavePluginDocument(StoreId, "settings", TraceJson.ToJson(settings));
        }

        public static bool IsPublic(TraceTurnContext turn) => turn?.Environment?.Visibility == "public";
        public static bool IsHumanInput(TraceTurnContext turn) => turn?.Moment != null &&
            turn.Services.Storage.LoadPairIdentity().IsHumanMoment(turn.Moment.Role);

        public static EnvironmentSnapshotData Resolve(IMemoryStore store, string root, PluginEventData source)
        {
            var observation = source.Environment;
            if (observation == null && source.PluginId == "builtin.dialogue" && store.LoadPairIdentity().IsHumanMoment(source.Role))
                observation = new EnvironmentObservationData { PlatformId = source.PluginId, SessionType = "private",
                    SessionId = "local", SpeakerId = "local-owner", ExclusivePair = true, DirectAddress = true };

            // 只有内核内部的后台事件可以恢复已绑定的目标；外部未知消息不继承上轮权限。
            if (observation == null && !store.LoadPairIdentity().IsHumanMoment(source.Role) &&
                source.PluginId is "builtin.time" or "runtime.execution" or "night.residue")
                observation = Observation(Read<EnvironmentSnapshotData>(store.LoadPluginDocument(StoreId, "target:" + root)));
            observation ??= new EnvironmentObservationData { PlatformId = source.PluginId };
            var internalEvent = !store.LoadPairIdentity().IsHumanMoment(source.Role) &&
                source.PluginId is "builtin.time" or "runtime.execution" or "night.residue";
            var platform = internalEvent && observation.PlatformId != null ? observation.PlatformId : source.PluginId;
            var accounts = Settings(store).OwnerAccounts ?? new();
            var local = platform == "builtin.dialogue" && observation.SpeakerId == "local-owner" && observation.ExclusivePair;
            var owner = local || accounts.Any(x => x.PlatformId == platform && x.UserId == observation.SpeakerId);
            var isPrivate = owner && observation.ExclusivePair && observation.SessionType == "private" &&
                !string.IsNullOrWhiteSpace(observation.SessionId);
            var id = platform + ":" + observation.AccountId + ":" + observation.SessionType + ":" + observation.SessionId;
            if (string.IsNullOrWhiteSpace(observation.SessionId)) id = platform + ":unknown:" + (source.ExternalEventId ?? Guid.NewGuid().ToString("N"));
            var rootId = Read<EnvironmentSnapshotData>(store.LoadPluginDocument(StoreId, "target:" + root))?.RootConversationId ?? root;
            var scope = isPrivate ? rootId : rootId + ":environment:" +
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).Substring(0, 24).ToLowerInvariant();
            return new EnvironmentSnapshotData
            {
                EnvironmentId = id, RootConversationId = rootId, ContextConversationId = scope,
                Visibility = isPrivate ? "private" : "public", SpeakerIsOwner = owner,
                OwnerPresent = owner || (observation.ParticipantIds ?? Array.Empty<string>()).Any(p =>
                    accounts.Any(x => x.PlatformId == platform && x.UserId == p)),
                AudienceConfirmed = observation.ExclusivePair || observation.SessionType == "group",
                PlatformId = platform, AccountId = observation.AccountId, SessionId = observation.SessionId,
                SessionType = observation.SessionType, SpeakerId = observation.SpeakerId,
                SpeakerName = observation.SpeakerName, ReplyToEventId = observation.ReplyToEventId,
                DirectAddress = observation.DirectAddress || observation.SessionType == "private",
                ParticipantIds = Array.AsReadOnly((observation.ParticipantIds ?? Array.Empty<string>()).Distinct().ToArray()),
                UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }

        public static void Remember(IMemoryStore store, EnvironmentSnapshotData environment)
        {
            store.SavePluginDocument(StoreId, "target:" + environment.ContextConversationId, TraceJson.ToJson(environment));
            // 根 runtime 保留最近的在场状态，私密后台目标单独保存，公开消息不会覆盖它。
            var runtime = store.LoadOrCreateInnerRuntime(environment.RootConversationId);
            runtime.Environment = environment;
            runtime.Revision++;
            runtime.SnapshotId = Guid.NewGuid().ToString("N");
            store.SaveInnerRuntime(runtime);
        }

        public static EnvironmentObservationData Observation(EnvironmentSnapshotData value) => value == null ? null : new()
        {
            PlatformId = value.PlatformId, AccountId = value.AccountId,
            SessionId = value.SessionId, SessionType = value.SessionType, SpeakerId = value.SpeakerId,
            SpeakerName = value.SpeakerName, ParticipantIds = value.ParticipantIds,
            ExclusivePair = value.SessionType == "private" && value.AudienceConfirmed,
            DirectAddress = value.DirectAddress, ReplyToEventId = value.ReplyToEventId
        };

        public static string PublicDialogue(MomentRecord moment)
        {
            var environment = FromMoment(moment);
            var speaker = moment.Role == "assistant" ? "我" :
                string.IsNullOrWhiteSpace(environment?.SpeakerName) ? "参与者" : environment.SpeakerName;
            var id = moment.Role == "assistant" ? "" : environment?.SpeakerId;
            return "[" + speaker + (string.IsNullOrWhiteSpace(id) ? "" : " · " + id) + "] " + (moment.Content ?? "").Trim();
        }

        public static EnvironmentSnapshotData FromMoment(MomentRecord moment) => Read<EnvironmentSnapshotData>(moment?.EnvironmentJson);
        private static T Read<T>(string text) where T : class
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return TraceJson.FromJson<T>(text); }
            catch (System.Text.Json.JsonException) { return null; }
        }

        public static bool CanUse(TraceTurnContext turn, TraceContributionDescriptorData descriptor)
        {
            if (turn?.Environment != null && !CanDeliver(turn.Services.Storage, turn.Environment)) return false;
            if (IsPublic(turn) && descriptor?.SupportsPublicEnvironment != true) return false;
            if (turn?.Environment == null || descriptor == null || descriptor.Kind != TraceContributionKindValues.Effector ||
                descriptor.Id == "dialogue.print") return true;
            if (!IsPublic(turn) && MouthLogic.OrganOf(descriptor) is not (BodyOrganValues.Text or BodyOrganValues.Voice or BodyOrganValues.Image or BodyOrganValues.Sticker)) return true;
            var body = MouthLogic.BodyOf(descriptor);
            if (string.IsNullOrWhiteSpace(body)) return true;
            return turn.Environment.AudienceConfirmed && body == MouthLogic.BodyOfPlugin(turn.Environment.PlatformId);
        }

        public static bool CanDeliver(IMemoryStore store, EnvironmentSnapshotData environment) =>
            environment == null || environment.Visibility != "private" ||
            environment.PlatformId == "builtin.dialogue" && environment.SpeakerId == "local-owner" ||
            Settings(store).OwnerAccounts.Any(x => x.PlatformId == environment.PlatformId && x.UserId == environment.SpeakerId);


        public static string PublicSystem(TraceTurnContext turn) => IdentityProjectionLogic.Build(turn);
    }
}
