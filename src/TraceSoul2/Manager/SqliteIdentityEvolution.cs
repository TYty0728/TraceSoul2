using System.Runtime.CompilerServices;
using System;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Util;

namespace TraceSoul2.Manager
{
    public sealed partial class SqliteMemoryManager
    {
        [MethodImpl(MethodImplOptions.Synchronized)]
        public void CommitPublicExperienceChecked(System.Collections.Generic.IEnumerable<string> ids,
            System.Collections.Generic.IEnumerable<BrainCognitionWriteData> writes, Action validate)
        {
            connection.RunInTransaction(() => {
                connection.Execute("UPDATE plugin_documents SET UpdatedUnixMs=UpdatedUnixMs WHERE 0");
                validate(); CommitPublicExperience(ids, writes);
            });
        }
        [MethodImpl(MethodImplOptions.Synchronized)]
        public void CommitIdentityEvolution(string root, string slot, IdentityEvolutionLogic.State state)
        {
            connection.RunInTransaction(() => {
                connection.Execute("UPDATE plugin_documents SET UpdatedUnixMs=UpdatedUnixMs WHERE 0");
                var card = LoadIdentityCards(root).Single(x => x.Slot == slot);
                var nodes = IdentityReorganizationLogic.Sources(this, root, slot, includeWeakened: true);
                var evidence = ReadIdentityReorganizationEvidence(nodes.Select(x => x.Id));
                var stamp = IdentityReorganizationLogic.Digest(new { Pair = LoadPairIdentity(), cardStamp = PuzzleViewLogic.Stamp(card),
                    sourcesStamp = IdentityEvolutionLogic.SourcesStamp(nodes),
                    evidenceStamp = IdentityReorganizationLogic.Digest(evidence.OrderBy(x => x.Id, StringComparer.Ordinal).ToList()) });
                if (card.Pinned || stamp != state.inputStamp) throw new InvalidOperationException("身份依据在提交前变化，未采用旧结果。");
                if (state.decision.effect == "changed" ||
                    (state.decision.effect == "unchanged" && !IdentityProjectionLogic.Current(card, GetCognitionNodes(5000))))
                {
                    var ids = state.decision.effect == "changed" ? state.proposal.cognition_ids : state.decision.basis_ids;
                    var source = ReadIdentityReorganizationEvidence(ids).Select(x => x.MomentId).Distinct()
                        .Chunk(256).SelectMany(GetEvidenceMoments).OrderByDescending(x => x.CreatedUnixMs).FirstOrDefault();
                    if (source == null) throw new InvalidOperationException("身份认识缺少真实经历来源。");
                    card = SaveDerivedIdentityCard(root, slot, state.decision.effect == "changed" ? state.proposal.body : card.Body, ids, source.Id);
                }
                state.cardStamp = PuzzleViewLogic.Stamp(card);
                SavePluginDocument(IdentityEvolutionLogic.StoreId, root + ":" + slot, TraceJson.ToJson(state));
            });
        }
    }
}
