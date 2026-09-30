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
    /// <summary>同一拼图的可见视图；共享批准绑定确切版本，不授权其私人证据。</summary>
    public static class PuzzleViewLogic
    {
        public const string StoreId = "kernel.puzzle";
        public static string Root(TraceTurnContext turn) => turn.Environment?.RootConversationId ?? turn.ConversationId;
        public static string Fingerprint(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TraceJson.ToJson(value))));
        public static string Stamp(CognitionSliceRecord node) => Fingerprint(new { node.Id, node.Revision, node.Summary,
            node.Domains, node.About, node.Scope, node.Exceptions, node.IdentitySlot, node.Status, node.ContextConversationId, node.SubjectKey });
        public static string Stamp(IdentityCardRecord card) => Fingerprint(new { card.Id, card.Revision, card.Body,
            card.Origin, card.CognitionSourcesJson });
        public static bool Shared(IMemoryStore store, string id, string stamp) =>
            store.LoadPluginDocument(StoreId, "share:" + id) == stamp;
        // Only the authenticated owner settings API calls this. Never exposed as a model capability.
        public static void SetShared(IMemoryStore store, string id, string stamp, bool shared) =>
            store.SavePluginDocument(StoreId, "share:" + id, shared ? stamp : "");

        public static bool CanRead(TraceTurnContext turn, CognitionSliceRecord node)
        {
            if (node == null) return false;
            var root = Root(turn);
            if (!string.IsNullOrEmpty(node.ContextConversationId) && node.ContextConversationId != root &&
                !node.ContextConversationId.StartsWith(root + ":environment:", StringComparison.Ordinal)) return false;
            return !EnvironmentLogic.IsPublic(turn) || node.ContextConversationId == turn.ConversationId ||
                Shared(turn.Services.Storage, node.Id, Stamp(node));
        }

        public static bool CanReadEvidence(TraceTurnContext turn, MomentRecord moment) => moment != null &&
            (!EnvironmentLogic.IsPublic(turn) ? string.IsNullOrWhiteSpace(moment.ConversationId) ||
                moment.ConversationId == Root(turn) || moment.ConversationId.StartsWith(Root(turn) + ":environment:", StringComparison.Ordinal)
                : moment.MemoryVisibility == "public" && moment.ConversationId == turn.ConversationId);

        public static T Read<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return TraceJson.FromJson<T>(json); }
            catch (System.Text.Json.JsonException) { return null; }
        }

        public static bool IdentitySlot(string slot) => string.IsNullOrEmpty(slot) ||
            slot is "self" or "personality" or "expression_habit" or "other" or "relation";

        public static string SubjectKey(MomentRecord moment)
        {
            var env = EnvironmentLogic.FromMoment(moment);
            return env == null ? "legacy:unresolved" : env.SpeakerIsOwner ? "owner" :
                env.PlatformId + ":" + env.SpeakerId;
        }
    }
}
