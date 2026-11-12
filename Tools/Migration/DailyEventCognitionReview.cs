using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Util;

namespace TraceSoul2.Migrate
{
    public static partial class DayBuilder
    {
        private static List<ReviewQuote> ResolveEventQuotes(ExtractedEvent entry, List<MomentRecord> sources)
        {
            var resolved = new List<ReviewQuote>();
            foreach (var text in (entry.quotes ?? new()).Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                var matches = entry.source_refs.Select(r => sources[r - 1])
                    .Where(m => (m.Content ?? "").Contains(text, StringComparison.Ordinal)).Take(2).ToList();
                // Optional excerpts only become quotes when both wording and speaker are grounded locally.
                if (matches.Count == 1) resolved.Add(new ReviewQuote { SourceId = matches[0].Id, Text = text });
            }
            return resolved;
        }

        private static async Task FormEventCognitions(MigrationContext context, DailyReviewState state,
            List<MomentRecord> sources, ILlmClient llm)
        {
            var events = state.Events.Where(e => state.Selected.Contains(e.Id) && !state.CognitionEvents.Contains(e.Id)).ToList();
            if (events.Count == 0) return;
            context.Migration.SetReviewStage((UsesSubjectiveReview(state) ? SubjectiveStage(state, 2, "形成认知") : "3/4 形成认知") + "：回顾 " + events.Count + " 件经历");
            // Original messages stay local for provenance and transaction validation, never in this request.
            var byId = sources.ToDictionary(m => m.Id);
            var evidenceIds = events.SelectMany(e => e.Sources).Distinct().ToHashSet();
            var evidence = sources.Where(m => evidenceIds.Contains(m.Id)).ToList();
            var conversations = evidence.Select(m => m.ConversationId ?? "").Distinct().ToList();
            var tagIds = events.SelectMany(e => e.TagIds).ToHashSet();
            var topics = context.Store.GetActiveLifeTags().Where(t => tagIds.Contains(t.Id)).ToList();
            var input = new Dictionary<string, object> {
                ["pair"] = context.RequirePair(),
                ["events"] = events.Select((e, i) => new {
                    event_ref = i + 1, summary = e.Summary,
                    conversation_ref = conversations.IndexOf(byId[e.Sources[0]].ConversationId ?? "") + 1,
                    sources = e.Sources.Select(id => byId[id]).Select(m => new {
                        role = m.Role, realm = m.Realm, evidence_type = m.EvidenceType, type = m.Type }).Distinct(),
                    quotes = (e.Quotes ?? new()).Select(q => new {
                        role = byId[q.SourceId].Role, text = q.Text, realm = byId[q.SourceId].Realm,
                        evidence_type = byId[q.SourceId].EvidenceType, type = byId[q.SourceId].Type }),
                    tag_refs = topics.Select((t, n) => (t, reference: n + 1))
                        .Where(x => e.TagIds.Contains(x.t.Id)).Select(x => x.reference)
                }),
                ["topics"] = topics.Select((t, i) => new { tag_ref = i + 1, name = t.Label, definition = t.Definition })
            };
            if (!UsesSubjectiveReview(state)) input["focus"] = state.SelectionFocus ?? "";
            var messages = new List<DeepSeekMessageData> {
                new("system", ReviewIdentity(state) + (UsesSubjectiveReview(state)
                    ? "【" + SubjectiveStage(state, 2, "形成认知") + "】回看我的整份一天速览及必要原话，按我在意的联系、变化与疑问，理解这些经历对自己、对方、我们和外界意味着什么。速览按我的关注程度排列，每件经历都已保留，在这里选择有依据、值得延续的理解，可以没有新认知。"
                    : "【3/4 形成认知】根据入选事件及关键原话，理解这些经历之间的联系、变化和含义。focus是上一环节提出的思考方向，结合事件判断。") +
                    "先把围绕同一问题的经历放在一起，辨明彼此印证、修正或保留分歧的地方，再写出值得延续的理解及其依据。每条以理解为中心，保留必要条件和例外。" +
                    CognitionFormationLogic.ReviewMeaningGuide +
                    "events是由完整对话整理的经历，sources说明参与者与事实性质，quotes保留关键原话；原始消息由程序保存并关联。" +
                    "每项event_refs填写支持该理解的事件编号，引用同一conversation_ref内的事件。tag_refs按含义从topics选择，可为空。" +
                    "operation填create，target_ref填0。domains为user/world/ass/relation的非空子集，self/personality/expression_habit须包含ass。" +
                    "返回JSON {\"cognitions\":[{\"operation\":\"create\",\"target_ref\":0,\"summary\":\"理解\",\"identity_slot\":\"relation\",\"domains\":[\"relation\"],\"event_refs\":[1,2],\"tag_refs\":[],\"scope\":\"\",\"exceptions\":\"\"}]}。无新理解返回{\"cognitions\":[]}。" + ReviewOutputContract(state)),
                new("user", TraceJson.ToJson(input))
            };
            List<BrainCognitionWriteData> mapped = null;
            string Error(CognitionOutput output)
            {
                if (output.cognitions == null) return "cognitions请填写数组；无新理解填写[]。";
                mapped = new();
                for (var i = 0; i < output.cognitions.Count; i++)
                {
                    var w = output.cognitions[i]; var path = "cognitions[" + i + "]";
                    if (w == null) return path + "请填写对象。";
                    if (w.operation != "create" || w.target_ref != 0) return path + "请使用create和target_ref=0。";
                    w.event_refs = w.event_refs?.Distinct().ToList(); w.tag_refs = w.tag_refs?.Distinct().ToList() ?? new();
                    var error = ReferenceError(w.event_refs, events.Count, path + ".event_refs");
                    if (error != null) return error;
                    error = ReferenceError(w.tag_refs, topics.Count, path + ".tag_refs", true);
                    if (error != null) return error;
                    var ids = w.event_refs.SelectMany(r => events[r - 1].Sources).Distinct().ToList();
                    if (ids.Select(id => byId[id].ConversationId ?? "").Distinct().Count() != 1)
                        return path + ".event_refs请引用同一conversation_ref内的事件。";
                    mapped.Add(new BrainCognitionWriteData {
                        operation = "create", summary = w.summary, identity_slot = w.identity_slot ?? "", domains = w.domains,
                        evidence_moment_ids = ids, tag_ids = w.tag_refs.Select(r => topics[r - 1].Id).ToList(),
                        scope = w.scope, exceptions = w.exceptions, confidence = .7f, strength = .5f
                    });
                }
                return CognitionFormationLogic.ValidationError(mapped, evidence, new List<CognitionSliceRecord>(), topics,
                    checkLength: false, completeBatch: true)?.Replace("evidence_moment_ids", "event_refs").Replace("tag_ids", "tag_refs");
            }
            var output = await DeepSeekStructuredOutputLogic.CompleteAsync<CognitionOutput>(llm, messages, null,
                "认知输出无效。", CancellationToken.None, validationError: Error, preserveValidContent: true);
            var finalError = Error(output);
            if (finalError != null) throw new InvalidOperationException(finalError);
            context.Store.CommitCognitionsWithCheckpoint(evidence[^1].Id, mapped, DailyReviewDocuments, state.Day,
                () => ValidateDailySources(context, state), changed => {
                    state.CognitionEvents.AddRange(events.Select(e => e.Id));
                    state.CognitionSources = state.CognitionSources.Concat(evidence.Select(m => m.Id)).Distinct().ToList();
                    state.ChangedCognitions = state.ChangedCognitions.Concat(changed.Select(n => n.Id)).Distinct().ToList();
                    return TraceJson.ToJson(state);
                }, completeBatch: true);
        }
    }
}
