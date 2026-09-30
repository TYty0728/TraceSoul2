using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Util;

namespace TraceSoul2.Manager
{
    public sealed partial class SqliteMemoryManager
    {
        public bool TrySaveSubject(string root, long expectedRevision, SubjectState state, InnerRuntimeData local = null)
        {
            var saved = false;
            connection.RunInTransaction(() =>
            {
                var current = PuzzleViewLogic.Read<SubjectState>(LoadPluginDocument(SubjectRuntimeLogic.StoreId, root));
                if ((current?.Revision ?? 0) != expectedRevision || state.Revision != expectedRevision + 1) return;
                if (local != null)
                {
                    var previousLocal = connection.Find<InnerRuntimeRecord>(local.ConversationId);
                    if (previousLocal != null && local.Revision != previousLocal.Revision + 1) return;
                    WriteInnerRuntime(local);
                }
                SavePluginDocument(SubjectRuntimeLogic.StoreId, root, TraceJson.ToJson(state));
                saved = true;
            });
            return saved;
        }

        public IdentityCardRecord SaveDerivedIdentityCard(string conversationId, string slot, string body,
            IEnumerable<string> cognitionIds, string sourceMomentId)
        {
            var ids = (cognitionIds ?? Array.Empty<string>()).Distinct().ToList();
            if (ids.Count == 0 || ids.Count > 12) throw new InvalidOperationException("成长摘要必须引用 1～12 条认知依据。");
            IdentityCardRecord result = null;
            connection.RunInTransaction(() =>
            {
                var current = connection.Find<IdentityCardRecord>(conversationId + "|" + slot);
                if (current?.Pinned == true) throw new InvalidOperationException("本人固定的身份内容不能被复盘覆盖。");
                var nodes = ids.Select(id => connection.Find<CognitionSliceRecord>(id)).ToList();
                if (nodes.Any(n => n == null || n.Status != "active" ||
                    (!string.IsNullOrEmpty(n.ContextConversationId) && n.ContextConversationId != conversationId) ||
                    n.MemoryVisibility == "public" || n.IdentitySlot != slot))
                    throw new InvalidOperationException("身份摘要引用了失效、跨环境或用途不符的认知。");
                if (nodes.Any(n => !connection.Table<CognitionEvidenceRecord>().Any(e => e.CognitionId == n.Id)))
                    throw new InvalidOperationException("身份摘要缺少经历来源。");
                result = SaveIdentityCard(conversationId, slot, body, sourceMomentId);
                result.Origin = "derived";
                result.CognitionSourcesJson = TraceJson.ToJson(nodes.ToDictionary(n => n.Id, PuzzleViewLogic.Stamp));
                connection.Update(result);
            });
            return result;
        }

        public void SetIdentityPinned(string conversationId, string slot, bool pinned)
        {
            var card = LoadIdentityCards(conversationId).Single(x => x.Slot == slot);
            card.Pinned = pinned;
            connection.Update(card);
        }

        public List<string> GetUnbuiltPublicMemoryDayKeysBefore(long endMs) => connection.QueryScalars<string>(
            "SELECT DISTINCT strftime('%Y-%m-%d', datetime(CreatedUnixMs/1000, 'unixepoch', '+8 hours', '-4 hours')) " +
            "FROM moments WHERE CreatedUnixMs<? AND MemoryVisibility='public' AND (MemoryStatus IS NULL OR MemoryStatus NOT IN ('built','operational')) ORDER BY 1", endMs);

        public List<MomentRecord> GetPublicMomentsInRange(long startMs, long endMs, int take = 200) => connection.Table<MomentRecord>()
            .Where(x => x.MemoryVisibility == "public" && x.CreatedUnixMs >= startMs && x.CreatedUnixMs < endMs &&
                (x.MemoryStatus == null || (x.MemoryStatus != "built" && x.MemoryStatus != "operational")))
            .OrderBy(x => x.CreatedUnixMs).Take(Math.Clamp(take, 1, 200)).ToList();

        public void CommitPublicExperience(IEnumerable<string> sourceIds, IEnumerable<BrainCognitionWriteData> writes)
        {
            var ids = sourceIds.Distinct().ToList();
            connection.RunInTransaction(() =>
            {
                var moments = ids.Select(id => connection.Find<MomentRecord>(id)).ToList();
                if (moments.Count == 0 || moments.Any(x => x == null || x.MemoryVisibility != "public") ||
                    moments.Select(x => x.ConversationId).Distinct().Count() != 1)
                    throw new InvalidOperationException("公开整理证据必须来自同一环境。");
                if (moments.All(x => x.MemoryStatus == "built")) return;
                if (moments.Any(x => x.MemoryStatus == "built")) throw new InvalidOperationException("公开证据批次已部分提交，请重读。");
                var changes = (writes ?? throw new InvalidOperationException("缺少认知变更数组。")).ToList();
                var candidates = GetCognitionNodes(5000).Where(x => x.ContextConversationId == moments[0].ConversationId && PuzzleDomains.Live(x.Status)).ToList();
                if (!CognitionFormationLogic.Valid(changes, moments, candidates, new List<LifeTagRecord>()) ||
                    (!moments.Any(m => EnvironmentLogic.FromMoment(m)?.SpeakerIsOwner == true) && changes.Any(w => (w.domains ?? new()).Contains("user"))))
                    throw new InvalidOperationException("公开认知变更的结构、对象或引用无效。");
                if (changes.Any(x => x.evidence_moment_ids == null || x.evidence_moment_ids.Count == 0 ||
                    x.evidence_moment_ids.Any(id => !ids.Contains(id)) || (x.evidence_fact_ids?.Count ?? 0) != 0))
                    throw new InvalidOperationException("公开整理不能引用未提供的证据。");
                CommitCognitions(ids[0], changes);
                MarkPublicMomentsBuilt(ids);
            });
        }

        public void MarkPublicMomentsBuilt(IEnumerable<string> ids)
        {
            connection.RunInTransaction(() =>
            {
                foreach (var id in ids.Distinct())
                {
                    var moment = connection.Find<MomentRecord>(id);
                    if (moment?.MemoryVisibility != "public") throw new InvalidOperationException("只可标记公开经历。");
                    moment.MemoryStatus = "built";
                    connection.Update(moment);
                }
            });
        }
    }
}
