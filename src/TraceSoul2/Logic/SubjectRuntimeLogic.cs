using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    public sealed class SubjectFragment
    {
        public string Text { get; set; }
        public string Context { get; set; }
        public string Visibility { get; set; }
        public string MomentId { get; set; }
        public long UpdatedUnixMs { get; set; }
    }
    public sealed class SubjectState
    {
        public long Revision { get; set; }
        public bool RootRuntime { get; set; }
        public string Affect { get; set; } = "calm";
        public SubjectFragment Narrative { get; set; }
        public SubjectFragment Mood { get; set; }
        public SubjectFragment Attention { get; set; }
        public SubjectFragment Scene { get; set; }
        public bool Asleep { get; set; }
        public bool Idle { get; set; }
        public string SourceMomentId { get; set; }
    }

    /// <summary>唯一主体 runtime；环境只过滤读取视图，片段来源不能被另一字段的更新公开。</summary>
    public static class SubjectRuntimeLogic
    {
        public const string StoreId = "kernel.subject";
        private sealed class TurnVersion { public long Revision; }
        public static bool ValidAffect(string value) => string.IsNullOrEmpty(value) ||
            value is "calm" or "joyful" or "sad" or "angry" or "anxious" or "tired" or "curious" or "mixed";
        public static string AffectLabel(string value) => value switch
        {
            "joyful" => "愉快", "sad" => "低落", "angry" => "生气", "anxious" => "不安",
            "tired" => "疲惫", "curious" => "好奇", "mixed" => "复杂", _ => "平静"
        };
        public static SubjectState Read(IMemoryStore store, string root)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var saved = PuzzleViewLogic.Read<SubjectState>(store.LoadPluginDocument(StoreId, root));
                var old = store.LoadOrCreateInnerRuntime(root);
                if (saved == null)
                {
                    SubjectFragment Legacy(string text) => new() { Text = text, Context = root, Visibility = "private",
                        MomentId = old.SourceMomentId, UpdatedUnixMs = old.UpdatedUnixMs };
                    return new SubjectState { RootRuntime = true, Narrative = Legacy(old.Narrative), Mood = Legacy(old.Mood),
                        Attention = Legacy(TraceJson.ToJson(old.Attention)), Scene = Legacy(old.OngoingActivity),
                        Asleep = old.Asleep, Idle = old.Idle };
                }
                if (!saved.RootRuntime)
                {
                    // 旧版将最新主体感受存成独立正文；一次性接回根runtime，之后这些片段只负责来源/可见性。
                    old.Narrative = saved.Narrative?.Text ?? old.Narrative;
                    old.Mood = saved.Mood?.Text ?? old.Mood;
                    old.Attention = PuzzleViewLogic.Read<List<AttentionItemData>>(saved.Attention?.Text) ?? old.Attention;
                    old.OngoingActivity = saved.Scene?.Text ?? old.OngoingActivity;
                    old.Asleep = saved.Asleep; old.Idle = saved.Idle;
                    old.UpdatedUnixMs = new[] { old.UpdatedUnixMs, saved.Narrative?.UpdatedUnixMs ?? 0,
                        saved.Mood?.UpdatedUnixMs ?? 0, saved.Attention?.UpdatedUnixMs ?? 0, saved.Scene?.UpdatedUnixMs ?? 0 }.Max();
                    old.Revision++; old.SnapshotId = Guid.NewGuid().ToString("N");
                    if (!string.IsNullOrWhiteSpace(saved.SourceMomentId)) old.SourceMomentId = saved.SourceMomentId;
                    var previous = saved.Revision;
                    saved.Revision++; saved.RootRuntime = true;
                    if (store is SqliteMemoryManager sqlite)
                    {
                        if (!sqlite.TrySaveSubject(root, previous, saved, old)) continue;
                    }
                    else { store.SaveInnerRuntime(old); store.SavePluginDocument(StoreId, root, TraceJson.ToJson(saved)); }
                }
                saved.Asleep = old.Asleep; saved.Idle = old.Idle;
                return saved;
            }
            throw new InvalidOperationException("主体runtime正在更新，未使用旧环境快照覆盖。");
        }
        public static void Begin(TraceTurnContext turn)
        {
            var current = Read(turn.Services.Storage, PuzzleViewLogic.Root(turn));
            turn.Workspace.GetOrCreateState(StoreId, () => new TurnVersion { Revision = current.Revision });
        }
        public static InnerRuntimeData View(TraceTurnContext turn)
        {
            return View(turn.Services.Storage, PuzzleViewLogic.Root(turn), turn.ConversationId,
                EnvironmentLogic.IsPublic(turn), EnvironmentLogic.IsHumanInput(turn));
        }

        public static InnerRuntimeData Current(TraceTurnContext turn)
        {
            Read(turn.Services.Storage, PuzzleViewLogic.Root(turn));
            return turn.Services.Storage.LoadOrCreateInnerRuntime(PuzzleViewLogic.Root(turn));
        }

        public static InnerRuntimeData View(IMemoryStore store, string root, string context, bool isPublic, bool humanInput = false)
        {
            var current = Read(store, root);
            var local = store.LoadOrCreateInnerRuntime(root);
            bool VisibleFragment(SubjectFragment fragment, string text) => !isPublic ||
                fragment != null && fragment.Context == context && fragment.Text == text;
            if (!VisibleFragment(current.Narrative, local.Narrative)) local.Narrative = "";
            if (!VisibleFragment(current.Mood, local.Mood)) local.Mood = AffectLabel(current.Affect);
            if (!VisibleFragment(current.Attention, TraceJson.ToJson(local.Attention))) local.Attention = new();
            // 历史环境快照不再作为当下来源。公开可隐藏同一场景，但不能恢复一份旧场景。
            if (!VisibleFragment(current.Scene, local.OngoingActivity)) local.OngoingActivity = "";
            if (isPublic) { local.RelationshipLens = ""; local.UnfinishedIntent = ""; }
            local.Asleep = current.Asleep && !humanInput;
            local.Idle = current.Idle && !humanInput;
            return local;
        }

        public static void Commit(TraceTurnContext turn, AgentStepData step)
        {
            if (turn.Environment == null || step == null) return;
            var store = turn.Services.Storage;
            var root = PuzzleViewLogic.Root(turn);
            var current = Read(store, root);
            var version = turn.Workspace.GetOrCreateState(StoreId, () => new TurnVersion { Revision = current.Revision });
            if (current.Revision != version.Revision) return; // 旧轮/日复盘不能覆盖已提交的新当下。
            var local = store.LoadOrCreateInnerRuntime(root);
            SubjectFragment Fragment(string text) => new() { Text = text, Context = turn.ConversationId,
                Visibility = turn.Environment.Visibility, MomentId = turn.Moment.Id,
                UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            if (!string.IsNullOrWhiteSpace(step.inner)) current.Narrative = Fragment(local.Narrative);
            if (step.HasStateField("mood") && !string.IsNullOrWhiteSpace(step.mood)) current.Mood = Fragment(local.Mood);
            if (step.HasStateField("attention")) current.Attention = Fragment(TraceJson.ToJson(local.Attention));
            if (step.ClearsScene() || !string.IsNullOrWhiteSpace(step.SceneValue())) current.Scene = Fragment(local.OngoingActivity);
            if (!string.IsNullOrEmpty(step.affect) && ValidAffect(step.affect)) current.Affect = step.affect;
            var presence = InnerLifeLogic.WithAsleep(local, step.sleep, turn.Moment.Id,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            current.Asleep = presence.Asleep;
            current.Idle = presence.Idle;
            current.SourceMomentId = turn.Moment.Id;
            current.Revision++;
            if (store is SqliteMemoryManager sqlite)
                sqlite.TrySaveSubject(root, version.Revision, current, presence == local ? null : presence);
            else
            {
                if (presence != local) store.SaveInnerRuntime(presence);
                store.SavePluginDocument(StoreId, root, TraceJson.ToJson(current));
            }
        }

        public static bool CommitReview(SqliteMemoryManager store, string root, long expectedRevision,
            InnerRuntimeData next, InnerRuntimeWriteData changes)
        {
            var current = Read(store, root);
            if (current.Revision != expectedRevision) return false;
            SubjectFragment Fragment(string text) => new() { Text = text, Context = root, Visibility = "private",
                MomentId = next.SourceMomentId, UpdatedUnixMs = next.UpdatedUnixMs };
            if (!string.IsNullOrWhiteSpace(changes.narrative)) current.Narrative = Fragment(next.Narrative);
            if (!string.IsNullOrWhiteSpace(changes.mood)) current.Mood = Fragment(next.Mood);
            if (changes.attention != null) current.Attention = Fragment(TraceJson.ToJson(next.Attention));
            current.SourceMomentId = next.SourceMomentId;
            current.Revision++;
            return store.TrySaveSubject(root, expectedRevision, current, next);
        }
    }
}
