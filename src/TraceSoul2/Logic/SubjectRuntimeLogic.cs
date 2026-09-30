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
        public string Affect { get; set; } = "calm";
        public SubjectFragment Narrative { get; set; }
        public SubjectFragment Mood { get; set; }
        public SubjectFragment Attention { get; set; }
        public bool Asleep { get; set; }
        public bool Idle { get; set; }
        public string SourceMomentId { get; set; }
    }

    /// <summary>连续主体状态与局部场景分开；每个感受的原因独立保留来源，不能被另一字段的更新公开。</summary>
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
            var saved = PuzzleViewLogic.Read<SubjectState>(store.LoadPluginDocument(StoreId, root));
            if (saved != null) return saved;
            var old = store.LoadOrCreateInnerRuntime(root);
            SubjectFragment Legacy(string text) => new() { Text = text, Context = root, Visibility = "private",
                MomentId = old.SourceMomentId, UpdatedUnixMs = old.UpdatedUnixMs };
            return new SubjectState { Narrative = Legacy(old.Narrative), Mood = Legacy(old.Mood),
                Attention = Legacy(TraceJson.ToJson(old.Attention)), Asleep = old.Asleep, Idle = old.Idle };
        }
        public static void Begin(TraceTurnContext turn)
        {
            var current = Read(turn.Services.Storage, PuzzleViewLogic.Root(turn));
            turn.Workspace.GetOrCreateState(StoreId, () => new TurnVersion { Revision = current.Revision });
        }
        public static InnerRuntimeData View(TraceTurnContext turn)
        {
            var local = turn.Services.Storage.LoadOrCreateInnerRuntime(turn.ConversationId);
            if (turn.Environment == null) return local;
            return View(turn.Services.Storage, PuzzleViewLogic.Root(turn), turn.ConversationId,
                EnvironmentLogic.IsPublic(turn), EnvironmentLogic.IsHumanInput(turn));
        }

        public static InnerRuntimeData View(IMemoryStore store, string root, string context, bool isPublic, bool humanInput = false)
        {
            var local = store.LoadOrCreateInnerRuntime(context);
            var current = Read(store, root);
            bool VisibleFragment(SubjectFragment fragment) => fragment != null && (!isPublic || fragment.Context == context);
            local.Narrative = VisibleFragment(current.Narrative) ? current.Narrative.Text : "";
            local.Mood = VisibleFragment(current.Mood) ? current.Mood.Text : AffectLabel(current.Affect);
            local.Attention = VisibleFragment(current.Attention)
                ? PuzzleViewLogic.Read<List<AttentionItemData>>(current.Attention.Text) ?? new() : new();
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
            var local = store.LoadOrCreateInnerRuntime(turn.ConversationId);
            SubjectFragment Fragment(string text) => new() { Text = text, Context = turn.ConversationId,
                Visibility = turn.Environment.Visibility, MomentId = turn.Moment.Id,
                UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            if (!string.IsNullOrWhiteSpace(step.inner)) current.Narrative = Fragment(local.Narrative);
            if (step.HasStateField("mood") && !string.IsNullOrWhiteSpace(step.mood)) current.Mood = Fragment(local.Mood);
            if (step.HasStateField("attention")) current.Attention = Fragment(TraceJson.ToJson(local.Attention));
            if (!string.IsNullOrEmpty(step.affect) && ValidAffect(step.affect)) current.Affect = step.affect;
            current.Asleep = step.sleep;
            current.Idle = local.Idle;
            current.SourceMomentId = turn.Moment.Id;
            current.Revision++;
            if (store is SqliteMemoryManager sqlite) sqlite.TrySaveSubject(root, version.Revision, current);
            else store.SavePluginDocument(StoreId, root, TraceJson.ToJson(current));
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
