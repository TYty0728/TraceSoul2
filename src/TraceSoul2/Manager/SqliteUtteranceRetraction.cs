using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Util;

namespace TraceSoul2.Manager
{
    public sealed partial class SqliteMemoryManager
    {
        private const string RetractionPlugin = "kernel.utterance-retraction";
        internal const string DailyReviewPlugin = "daily-review";

        [MethodImpl(MethodImplOptions.Synchronized)]
        public MomentRecord GetLatestPrivateUtterance(string conversationId)
        {
            var pair = LoadPairIdentity();
            return connection.Query<MomentRecord>(
                "SELECT * FROM moments WHERE ConversationId=? AND IFNULL(MemoryVisibility,'')!='public' " +
                "AND IFNULL(MemoryStatus,'')!='operational' AND trim(IFNULL(Content,''))!='' " +
                "AND ((Type IN ('message','hearing') AND trim(Role) COLLATE NOCASE IN ('user',?,?)) " +
                "OR (Type='expression' AND trim(Role) COLLATE NOCASE IN ('assistant','ass',?))) " +
                "ORDER BY CreatedUnixMs DESC,rowid DESC LIMIT 1", conversationId, pair.Username, pair.CallName, pair.Assname)
                .FirstOrDefault();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool WasUtteranceTurnRetracted(string origin) =>
            !string.IsNullOrEmpty(origin) && !string.IsNullOrEmpty(LoadPluginDocument(RetractionPlugin, origin));

        internal bool WasScheduleRetracted(string scheduleId, string origin, bool heartbeat)
        {
            var marker = string.IsNullOrEmpty(origin) ? "" : LoadPluginDocument(RetractionPlugin, origin);
            return (heartbeat ? marker.Length > 0 : marker == "\"user\"") ||
                (!string.IsNullOrEmpty(scheduleId) && WasUtteranceTurnRetracted("schedule:" + scheduleId));
        }

        internal bool WasInboundUtteranceRetracted(string conversation, string plugin, string eventId, EnvironmentSnapshotData environment)
            => !string.IsNullOrWhiteSpace(eventId) && WasUtteranceTurnRetracted(InboundKey(conversation, plugin, eventId, environment));

        private static string InboundKey(string conversation, string plugin, string eventId, EnvironmentSnapshotData environment)
            => "inbound:" + IdentityReorganizationLogic.Digest(new {
                conversation, plugin, eventId, account = environment?.AccountId ?? "", session = environment?.SessionId ?? ""
            });

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool RetractLatestPrivateUtterance(string conversationId, string expectedId)
        {
            if (string.IsNullOrWhiteSpace(expectedId)) return false;
            var changed = false;
            connection.RunInTransaction(() =>
            {
                var latest = GetLatestPrivateUtterance(conversationId);
                if (latest?.Id != expectedId) return;
                var assistant = latest.Type == MomentTypes.Expression;
                var origin = assistant ? FindExpressionOrigin(latest) : latest.Id;
                var deleted = new HashSet<string>(StringComparer.Ordinal) { latest.Id };
                foreach (var id in connection.QueryScalars<string>("SELECT Id FROM moments WHERE ParentMomentId=? " +
                    "AND ConversationId=? AND Type IN ('sight','feeling','touch','event')", latest.Id, conversationId))
                    deleted.Add(id);
                var affectedState = deleted.ToHashSet(StringComparer.Ordinal);
                if (origin != null) affectedState.Add(origin);

                var cognition = new HashSet<string>(StringComparer.Ordinal);
                var events = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in deleted)
                {
                    events.UnionWith(connection.QueryScalars<string>("SELECT Id FROM event_indexes WHERE FirstMomentId=? " +
                        "UNION SELECT IndexId FROM event_entries WHERE SourceMomentId=?", id, id));
                    cognition.UnionWith(connection.QueryScalars<string>("SELECT CognitionId FROM cognition_evidence WHERE MomentId=? " +
                        "OR FactId IN (SELECT Id FROM fact_slices WHERE SourceMomentId=?)", id, id));
                }
                PruneRetractionCheckpoints(deleted, cognition, events);
                foreach (var id in events)
                {
                    connection.Execute("DELETE FROM ladder_items WHERE RefKind='event_index' AND RefId=?", id);
                    connection.Execute("DELETE FROM event_entries WHERE IndexId=?", id);
                    connection.Execute("DELETE FROM event_indexes WHERE Id=?", id);
                }
                foreach (var id in cognition)
                {
                    connection.Execute("DELETE FROM ladder_items WHERE RefKind='cognition' AND RefId=?", id);
                    connection.Execute("DELETE FROM cognition_evidence WHERE CognitionId=?", id);
                    connection.Execute("DELETE FROM cognition_tag_links WHERE CognitionId=?", id);
                    connection.Execute("DELETE FROM cognition_cues WHERE CognitionId=?", id);
                    connection.Execute("DELETE FROM cognition_edges WHERE FromCognitionId=? OR ToCognitionId=?", id, id);
                    connection.Execute("DELETE FROM cognition_slices WHERE Id=?", id);
                }
                foreach (var id in deleted)
                {
                    connection.Execute("DELETE FROM fact_tag_links WHERE FactId IN (SELECT Id FROM fact_slices WHERE SourceMomentId=?)", id);
                    connection.Execute("DELETE FROM fact_wakes WHERE TriggerMomentId=? OR FactId IN (SELECT Id FROM fact_slices WHERE SourceMomentId=?)", id, id);
                    connection.Execute("DELETE FROM fact_slices WHERE SourceMomentId=?", id);
                    connection.Execute("DELETE FROM cognition_cues WHERE SourceMomentId=?", id);
                    connection.Execute("DELETE FROM memory_observation_runs WHERE MomentId=?", id);
                    connection.Execute("UPDATE life_tags SET Status='retired' WHERE SourceMomentId=?", id);
                    connection.Execute("DELETE FROM moments WHERE Id=?", id);
                }
                foreach (var id in affectedState)
                {
                    RememberRetractedSchedules(id, assistant);
                    connection.Execute("DELETE FROM day_trajectory_entries WHERE SourceMomentId=?", id);
                    connection.Execute("DELETE FROM today_new_items WHERE SourceMomentId=?", id);
                    connection.Execute("DELETE FROM turn_reviews WHERE TriggerMomentId=?", id);
                    connection.Execute("DELETE FROM runtime_slices WHERE MomentId=?", id);
                    connection.Execute("UPDATE runtime_slices SET ReviewId=NULL WHERE ReviewId IN " +
                        "(SELECT Id FROM runtime_day_reviews WHERE instr(MomentIdsJson,?)>0)", TraceJson.ToJson(id));
                    connection.Execute("DELETE FROM runtime_day_reviews WHERE instr(MomentIdsJson,?)>0", TraceJson.ToJson(id));
                    connection.Execute("DELETE FROM runtime_day_summaries WHERE instr(MomentIdsJson,?)>0", TraceJson.ToJson(id));
                }
                var day = MemoryDayLogic.CurrentDayKey(DateTimeOffset.FromUnixTimeMilliseconds(latest.CreatedUnixMs));
                connection.Execute("DELETE FROM day_trajectory WHERE DayKey=? AND UpdatedUnixMs>=?", day, latest.CreatedUnixMs);
                connection.Execute("DELETE FROM day_trajectory_entries WHERE ConversationId=? AND DayKey=? AND SourceMomentId=? " +
                    "AND CreatedUnixMs>=?", conversationId, day, "legacy:" + day, latest.CreatedUnixMs);
                if (origin != null)
                {
                    GoalMemoryLogic.RetractSource(this, conversationId, origin, keepUserChanges: assistant);
                    SavePluginDocument(RetractionPlugin, origin, assistant ? "\"assistant\"" : "\"user\"");
                }
                if (!assistant && !string.IsNullOrWhiteSpace(latest.SourceEventId))
                    SavePluginDocument(RetractionPlugin, InboundKey(conversationId, latest.SourcePluginId, latest.SourceEventId,
                        EnvironmentLogic.FromMoment(latest)), "{}");
                RetractDerivedCards(deleted, cognition);
                RetractRuntime(conversationId, affectedState, cognition, events);
                changed = true;
            });
            return changed;
        }

        private void RememberRetractedSchedules(string origin, bool assistant)
        {
            foreach (var review in connection.Query<TurnReviewRecord>("SELECT * FROM turn_reviews WHERE TriggerMomentId=?", origin))
            {
                var snapshot = PuzzleViewLogic.Read<TurnPayloadSnapshotData>(review.PayloadJson);
                foreach (var result in snapshot?.results ?? new List<TurnResultSnapshotData>())
                {
                    if (result.status != "success" || (result.capability_id != "time.continue" &&
                        (assistant || result.capability_id != "time.schedule"))) continue;
                    var first = result.payload?.Split('|', 2)[0].Trim();
                    if (Guid.TryParseExact(first, "N", out _)) SavePluginDocument(RetractionPlugin, "schedule:" + first, "{}");
                }
            }
        }

        private string FindExpressionOrigin(MomentRecord expression)
        {
            if (!string.IsNullOrWhiteSpace(expression.ParentMomentId)) return expression.ParentMomentId;
            // Legacy sends did not store a parent. Match the recorded text in the completing turn.
            return connection.Query<TurnReviewRecord>("SELECT * FROM turn_reviews WHERE ConversationId=? AND CreatedUnixMs>=? " +
                "ORDER BY CreatedUnixMs,rowid", expression.ConversationId, expression.CreatedUnixMs)
                .FirstOrDefault(review =>
                {
                    var snapshot = PuzzleViewLogic.Read<TurnPayloadSnapshotData>(review.PayloadJson);
                    return snapshot?.results?.Any(x => x.status == "success" &&
                        x.capability_id is "dialogue.send" or "qq.text.send" or "wxxz.send" &&
                        x.payload == expression.Content) == true || snapshot?.agent_decision?.reply == expression.Content;
                })?.TriggerMomentId;
        }

        internal string DailyReviewSourceStamp(IEnumerable<string> ids)
        {
            var wanted = ids.ToHashSet(StringComparer.Ordinal);
            var sources = wanted.Chunk(256).SelectMany(GetEvidenceMoments).ToList();
            if (sources.Count != wanted.Count)
                throw new InvalidOperationException("复盘检查点的原始来源已缺失，不能继续提交。");
            return IdentityReorganizationLogic.Digest(new { Pair = LoadPairIdentity(), Sources = sources.OrderBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => new { x.Id, x.ConversationId, x.Content, x.CreatedUnixMs, x.Realm,
                    x.Role, x.EvidenceType, x.SourcePluginId, x.SourceEventId, x.PayloadJson,
                    x.EnvironmentJson, x.MemoryVisibility, x.Type, x.SourcePlatform, x.ParentMomentId }) });
        }

        private void PruneRetractionCheckpoints(HashSet<string> deleted, HashSet<string> cognition, HashSet<string> events)
        {
            var documents = deleted.SelectMany(id => connection.Query<PluginDocumentRecord>(
                "SELECT * FROM plugin_documents WHERE PluginId=? AND instr(Json,?)>0", DailyReviewPlugin, TraceJson.ToJson(id)))
                .DistinctBy(x => x.Id);
            foreach (var document in documents)
            {
                var state = JsonNode.Parse(document.Json).AsObject();
                var sources = state["SourceIds"]?.AsArray();
                if (sources == null || !sources.Any(x => deleted.Contains(x?.GetValue<string>()))) continue;
                RemoveIds(sources, deleted);
                var reread = deleted.ToHashSet(StringComparer.Ordinal);
                if (state["JointDraft"]?["SourceIds"] is JsonArray draftSources &&
                    draftSources.Any(x => deleted.Contains(x?.GetValue<string>())))
                {
                    reread.UnionWith(draftSources.Select(x => x.GetValue<string>()));
                    state["JointDraft"] = null;
                }
                var removedEntries = new HashSet<string>();
                if (state["Events"] is JsonArray rows)
                    foreach (var row in rows.Where(x => x?["Sources"] is JsonArray refs &&
                        refs.Any(r => reread.Contains(r?.GetValue<string>())) ||
                        events.Contains(x?["IndexId"]?.GetValue<string>())).ToList())
                    {
                        var index = row["IndexId"]?.GetValue<string>();
                        var entry = row["Id"]?.GetValue<string>();
                        if (index != null) events.Add(index);
                        if (entry != null) removedEntries.Add(entry);
                        rows.Remove(row);
                    }
                RemoveIds(state["Extracted"] as JsonArray, reread);
                RemoveIds(state["Selected"] as JsonArray, removedEntries);
                RemoveIds(state["CognitionEvents"] as JsonArray, removedEntries);
                RemoveIds(state["CognitionSources"] as JsonArray, deleted);
                RemoveIds(state["ChangedCognitions"] as JsonArray, cognition);
                state["Reflection"] = null;
                state["ReflectionStamp"] = null;
                state["RuntimeDraft"] = null;
                state["RuntimeDraftStamp"] = null;
                state["Selections"] = new JsonObject();
                if (!(state["Done"]?.GetValue<bool>() ?? false)) state["Selected"] = null;
                var engine = state["CognitionEngine"]?.GetValue<string>();
                state["Stamp"] = (engine is "batch-v1" or "jev-v1" ? engine + ":" : "") +
                    DailyReviewSourceStamp(sources.Select(x => x.GetValue<string>()));
                SavePluginDocument(DailyReviewPlugin, document.DocumentKey, state.ToJsonString());
            }
        }

        private static void RemoveIds(JsonArray values, HashSet<string> ids)
        {
            if (values == null) return;
            for (var i = values.Count - 1; i >= 0; i--)
                if (ids.Contains(values[i]?.GetValue<string>())) values.RemoveAt(i);
        }

        private void RetractDerivedCards(HashSet<string> deleted, HashSet<string> cognition)
        {
            bool Affected(IdentityCardRecord card) => deleted.Contains(card.SourceMomentId) ||
                (PuzzleViewLogic.Read<Dictionary<string, string>>(card.CognitionSourcesJson)?.Keys.Any(cognition.Contains) ?? false);
            var cards = connection.Table<IdentityCardRecord>().Where(x => x.Origin == "derived" && !x.Pinned).ToList();
            foreach (var card in cards.Where(Affected))
            {
                var prior = connection.Query<PluginDocumentRecord>("SELECT * FROM plugin_documents WHERE PluginId='kernel.identity.history' " +
                    "AND substr(DocumentKey,1,?)=?", card.Id.Length + 1, card.Id + ":")
                    .Select(x => PuzzleViewLogic.Read<IdentityCardRecord>(x.Json))
                    .Where(x => x?.Id == card.Id && x.Revision < card.Revision && !Affected(x))
                    .OrderByDescending(x => x.Revision).ThenByDescending(x => x.UpdatedUnixMs)
                    .FirstOrDefault(x => IdentityProjectionLogic.Current(x,
                        (PuzzleViewLogic.Read<Dictionary<string, string>>(x.CognitionSourcesJson) ?? new()).Keys
                        .Select(id => connection.Find<CognitionSliceRecord>(id)).Where(n => n != null)));
                card.Body = prior?.Body ?? "";
                card.Origin = prior?.Origin ?? card.Origin;
                card.CognitionSourcesJson = prior?.CognitionSourcesJson;
                card.SourceMomentId = prior?.SourceMomentId;
                card.Revision++;
                connection.Update(card);
            }
        }

        private void RetractRuntime(string root, HashSet<string> affected, HashSet<string> cognition, HashSet<string> events)
        {
            var row = connection.Find<InnerRuntimeRecord>(root);
            if (row == null) return;
            var state = PuzzleViewLogic.Read<SubjectState>(LoadPluginDocument(SubjectRuntimeLogic.StoreId, root));
            var next = ToRuntimeData(row);
            var latestAffected = affected.Contains(row.SourceMomentId);
            bool Clear(SubjectFragment fragment) => fragment == null ? latestAffected : affected.Contains(fragment.MomentId);
            if (Clear(state?.Narrative)) { next.Narrative = ""; if (state != null) state.Narrative = null; }
            if (Clear(state?.Mood)) { next.Mood = ""; if (state != null) { state.Mood = null; state.Affect = "calm"; } }
            if (Clear(state?.Scene)) { next.OngoingActivity = ""; if (state != null) state.Scene = null; }
            if (Clear(state?.Attention)) { next.Attention.Clear(); if (state != null) state.Attention = null; }
            else if (next.Attention.RemoveAll(x => x.source_refs?.Any(r => affected.Any(id => r == "moment:" + id) ||
                cognition.Any(id => r == "cognition:" + id) || events.Any(id => r == "event:" + id)) == true) > 0 && state?.Attention != null)
                state.Attention.Text = TraceJson.ToJson(next.Attention);
            if (latestAffected)
            {
                next.SourceMomentId = null;
                next.RelationshipLens = "";
                next.UnfinishedIntent = "";
            }
            next.Revision++;
            next.SnapshotId = Guid.NewGuid().ToString("N");
            next.UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            WriteInnerRuntime(next);
            if (state == null) return;
            state.Revision++;
            if (affected.Contains(state.SourceMomentId)) state.SourceMomentId = null;
            SavePluginDocument(SubjectRuntimeLogic.StoreId, root, TraceJson.ToJson(state));
        }
    }
}
