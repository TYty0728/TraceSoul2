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
            !MomentTypes.IsObservation(turn.Moment.Type) &&
            turn.Services.Storage.LoadPairIdentity().IsHumanMoment(turn.Moment.Role);

        public static EnvironmentSnapshotData Resolve(IMemoryStore store, string root, PluginEventData source)
        {
            var observation = source.Environment;
            if (observation == null && source.PluginId == "builtin.dialogue" && store.LoadPairIdentity().IsHumanMoment(source.Role))
                observation = new EnvironmentObservationData { PlatformId = source.PluginId, SessionType = "private",
                    SessionId = "local", SpeakerId = "local-owner", ExclusivePair = true, DirectAddress = true };

            var internalEvent = !store.LoadPairIdentity().IsHumanMoment(source.Role) &&
                source.PluginId is "builtin.time" or "runtime.execution" or "night.residue";
            // 自主唤醒使用独立确认的QQ目标；板端最后一次发言不转成主动播音许可。
            // 执行回执保留明确的原请求环境；缺少原请求时也不能借用最后一次板端目标。
            if (internalEvent && (observation == null ||
                source.PluginId != "runtime.execution" && MouthLogic.BodyOfPlugin(observation.PlatformId) != BodyIds.Qq))
            {
                var target = ProactiveTarget(store, root);
                var explicitDebug = target == null && observation?.PlatformId == "builtin.dialogue" &&
                    observation.SpeakerId == "local-owner" && observation.ExclusivePair &&
                    observation.SessionType == "private" && !string.IsNullOrWhiteSpace(observation.SessionId) &&
                    !Settings(store).OwnerAccounts.Any(x => MouthLogic.BodyOfPlugin(x.PlatformId) == BodyIds.Qq);
                if (!explicitDebug) observation = Observation(target);
            }
            observation ??= new EnvironmentObservationData { PlatformId = source.PluginId };
            var platform = internalEvent && observation.PlatformId != null ? observation.PlatformId : source.PluginId;
            var accounts = Settings(store).OwnerAccounts ?? new();
            var local = platform == "builtin.dialogue" && observation.SpeakerId == "local-owner" && observation.ExclusivePair;
            var owner = local || accounts.Any(x => x.PlatformId == platform && x.UserId == observation.SpeakerId);
            var isPrivate = owner && observation.ExclusivePair && observation.SessionType == "private" &&
                !string.IsNullOrWhiteSpace(observation.SessionId);
            var id = platform + ":" + observation.AccountId + ":" + observation.SessionType + ":" + observation.SessionId;
            if (string.IsNullOrWhiteSpace(observation.SessionId)) id = platform + ":unknown:" + (source.ExternalEventId ?? Guid.NewGuid().ToString("N"));
            var rootId = Read<EnvironmentSnapshotData>(store.LoadPluginDocument(StoreId, "target:" + root))?.RootConversationId ?? root;
            // 平台只是入口。所有本人私密交流共用根作用域；公开作用域只用于来源/可见视图。
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
            // 兼容升级前保存的QQ目标，须在本轮板端/控制台覆盖根目标之前保全。
            var root = environment.RootConversationId;
            var previous = Read<EnvironmentSnapshotData>(store.LoadPluginDocument(StoreId, "target:" + root));
            if (ValidProactiveQq(store, root, previous))
                store.SavePluginDocument(StoreId, "proactive-qq:" + root, TraceJson.ToJson(previous));
            if (ValidProactiveQq(store, root, environment))
                store.SavePluginDocument(StoreId, "proactive-qq:" + root, TraceJson.ToJson(environment));
            store.SavePluginDocument(StoreId, "target:" + environment.ContextConversationId, TraceJson.ToJson(environment));
            // 根 runtime 保留最近的在场状态，私密后台目标单独保存，公开消息不会覆盖它。
            var runtime = store.LoadOrCreateInnerRuntime(environment.RootConversationId);
            runtime.Environment = environment;
            runtime.Revision++;
            runtime.SnapshotId = Guid.NewGuid().ToString("N");
            store.SaveInnerRuntime(runtime);
        }

        private static bool ValidProactiveQq(IMemoryStore store, string root, EnvironmentSnapshotData target) =>
            target != null && target.RootConversationId == root && target.Visibility == "private" &&
            target.SessionType == "private" && !string.IsNullOrWhiteSpace(target.SessionId) &&
            target.SpeakerIsOwner && target.AudienceConfirmed && MouthLogic.BodyOfPlugin(target.PlatformId) == BodyIds.Qq &&
            CanDeliver(store, target);

        private static EnvironmentSnapshotData ProactiveTarget(IMemoryStore store, string root)
        {
            var saved = Read<EnvironmentSnapshotData>(store.LoadPluginDocument(StoreId, "proactive-qq:" + root));
            if (ValidProactiveQq(store, root, saved)) return saved;
            var legacy = Read<EnvironmentSnapshotData>(store.LoadPluginDocument(StoreId, "target:" + root));
            if (ValidProactiveQq(store, root, legacy)) return legacy;
            // 只有未配置QQ的本地调试会话可继续使用控制台；不能回退到开发板或其他真人目标。
            return !Settings(store).OwnerAccounts.Any(x => MouthLogic.BodyOfPlugin(x.PlatformId) == BodyIds.Qq) &&
                legacy?.RootConversationId == root && legacy.PlatformId == "builtin.dialogue" &&
                legacy.SpeakerId == "local-owner" && legacy.Visibility == "private" ? legacy : null;
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
            if (moment.Role == MomentTypes.PerceptionRole || MomentTypes.IsObservation(moment.Type))
                return "[感知 · " + MomentTypes.PlatformLabel(moment.SourcePlatform) + "] " + MomentTypes.ForPrompt(moment);
            var environment = FromMoment(moment);
            var speaker = moment.Role == "assistant" ? "我" :
                string.IsNullOrWhiteSpace(environment?.SpeakerName) ? "参与者" : environment.SpeakerName;
            var id = moment.Role == "assistant" ? "" : environment?.SpeakerId;
            return "[" + speaker + (string.IsNullOrWhiteSpace(id) ? "" : " · " + id) + "] " + (moment.Content ?? "").Trim();
        }

        public static string MomentSpeaker(MomentRecord moment, PairIdentity pair)
        {
            if (moment.Role == MomentTypes.PerceptionRole || MomentTypes.IsObservation(moment.Type)) return "感知";
            if (moment.MemoryVisibility == "public" && pair.IsHumanMoment(moment.Role))
            {
                var name = FromMoment(moment)?.SpeakerName;
                return string.IsNullOrWhiteSpace(name) ? "参与者" : name;
            }
            return pair.LabelForRole(moment.Role);
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
            // A physical-body turn cannot fall through to QQ tools/media (or vice versa).
            if (turn.Environment.PlatformId == "wxxz" || MouthLogic.BodyOf(descriptor, turn) == "wxxz")
                return turn.Environment.AudienceConfirmed && MouthLogic.BodyOf(descriptor, turn) == MouthLogic.BodyOfPlugin(turn.Environment.PlatformId);
            if (!IsPublic(turn) && MouthLogic.OrganOf(descriptor) is not (BodyOrganValues.Text or BodyOrganValues.Voice or BodyOrganValues.Image or BodyOrganValues.Sticker)) return true;
            var body = MouthLogic.BodyOf(descriptor, turn);
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
