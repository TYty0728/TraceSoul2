using System.Runtime.CompilerServices;
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
        [MethodImpl(MethodImplOptions.Synchronized)]
        public List<CognitionSliceRecord> ReadIdentityReorganizationNodes()
        {
            if (connection.Table<CognitionSliceRecord>().Count() > 5000)
                throw new IdentityReorganizationException("认知总量超过重整容量，已停止，未省略来源。");
            return GetCognitionNodes(5000);
        }
        [MethodImpl(MethodImplOptions.Synchronized)]
        public List<CognitionEvidenceRecord> ReadIdentityReorganizationEvidence(IEnumerable<string> ids)
            => ids.Distinct().SelectMany(id => connection.Table<CognitionEvidenceRecord>().Where(x => x.CognitionId == id).ToList()).ToList();
        [MethodImpl(MethodImplOptions.Synchronized)]
        public void CommitIdentityReorganization(IdentityReorganizationLogic.Draft draft, Action validate)
        {
            connection.RunInTransaction(() => {
                connection.Execute("UPDATE plugin_documents SET UpdatedUnixMs=UpdatedUnixMs WHERE 0");
                validate();
                var evidence = ReadIdentityReorganizationEvidence(draft.proposal.cognition_ids);
                var moments = evidence.Select(x => x.MomentId).Distinct().Chunk(256).SelectMany(GetEvidenceMoments).ToList();
                var source = moments.OrderByDescending(x => x.CreatedUnixMs).FirstOrDefault();
                if (source == null) throw new IdentityReorganizationException("候选缺少可核对的原始经历来源。");
                var card = SaveDerivedIdentityCard(draft.root, draft.slot, draft.proposal.body,
                    draft.proposal.cognition_ids, source.Id);
                draft.appliedCardStamp = PuzzleViewLogic.Stamp(card);
                draft.graphStamp = IdentityReorganizationLogic.GraphStamp(this);
                draft.cardsStamp = IdentityReorganizationLogic.Digest(LoadIdentityCards(draft.root));
                draft.status = "applied";
                IdentityReorganizationLogic.Save(this, draft);
                SavePluginDocument(IdentityReorganizationLogic.StoreId, "audit:" + draft.id, TraceJson.ToJson(draft));
                if (!string.IsNullOrEmpty(draft.day)) SavePluginDocument(IdentityReorganizationLogic.StoreId,
                    "receipt:" + draft.day + ":" + draft.slot, TraceJson.ToJson(draft));
            });
        }
    }
}
