using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Plugins;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 认知网活体写入口：这一拍真的改了看法才落一条切片。短卡修订仍只走潜意识。
    /// </summary>
    public static class CognitionLiveWriteLogic
    {
        public static List<CognitionSliceRecord> TryCommit(TraceTurnContext turn, MindDecisionData mind)
        {
            if (turn == null || turn.Services == null || turn.Services.Storage == null) return null;
            mind = MindLogic.Normalize(mind);
            var summary = Limit(OneLine(mind.cognition), 19);
            if (summary.Length == 0) return null;

            var tagIds = MemoryLiveWriteLogic.ResolveTagIds(turn, mind);
            if (tagIds.Count == 0)
            {
                var live = turn.Workspace.GetOrCreateState(
                    MemoryLiveWriteLogic.StateKey, () => new MemoryLiveWriteLogic.State());
                if (live.Commit != null && live.Commit.SelectedTags != null)
                    tagIds = live.Commit.SelectedTags.Where(x => x != null).Select(x => x.Id).ToList();
            }
            if (tagIds.Count == 0) return new List<CognitionSliceRecord>();

            var existing = (turn.Services.Storage.GetCognitionCandidates(tagIds, 20) ??
                            new List<CognitionSliceRecord>())
                .FirstOrDefault(x => x != null &&
                                     string.Equals(OneLine(x.Summary), summary, StringComparison.Ordinal));
            BrainCognitionWriteData mutation;
            if (existing != null)
            {
                mutation = new BrainCognitionWriteData
                {
                    operation = CognitionOperationValues.Reinforce,
                    target_id = existing.Id,
                    confidence = Math.Min(1f, existing.Confidence + 0.05f),
                    tag_ids = tagIds
                };
            }
            else
            {
                mutation = new BrainCognitionWriteData
                {
                    operation = CognitionOperationValues.Create,
                    summary = summary,
                    subtype = "standard",
                    confidence = 0.72f,
                    tag_ids = tagIds
                };
            }

            var changed = turn.Services.Storage.CommitCognitions(turn.Moment.Id, new[] { mutation });
            if (changed != null && changed.Count > 0)
            {
                turn.Workspace.Results.Add(new TraceCapabilityResultData
                {
                    CallId = "cognition-live",
                    CapabilityId = "cognition.live",
                    Status = "success",
                    Summary = existing == null
                        ? "这一拍写下一条看法：" + changed[0].Summary
                        : "这一拍加强了已有看法：" + changed[0].Summary,
                    Payload = changed[0].Summary
                });
            }
            return changed;
        }

        private static string OneLine(string value)
        {
            return string.Join(" ", (value ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
