using System;
using System.Collections.Generic;
using TraceSoul2.Data;

namespace TraceSoul2.Manager
{
    /// <summary>第四层五个交叉维度当前的全部取值列表（供记忆神经子代理做定位选择）。</summary>
    public sealed class EventDimensionValuesData
    {
        public List<string> TimeLabels = new List<string>();
        public List<string> DayKindLabels = new List<string>();
        public List<string> PlaceLabels = new List<string>();
        public List<string> PersonLabels = new List<string>();
        public List<string> MoodLabels = new List<string>();
        public List<string> MonthBuckets = new List<string>();
        public int TotalIndexes;
    }

    /// <summary>
    /// 宿主持久层口（插件可见的记忆存取面）。实现由宿主提供（SqliteMemoryManager 等），
    /// 外部插件只在 PluginApi 契约上编程，不接触具体存储。
    /// </summary>
    public interface IMemoryStore
    {
        void SaveMoment(MomentRecord moment);
        List<MomentRecord> GetRecentMoments(string conversationId, int take);
        List<TurnReviewRecord> GetRecentTurnReviews(string conversationId, int take);
        void SeedLifeTags(IEnumerable<VectorIndexNode> ontology);
        List<LifeTagRecord> GetActiveLifeTags();
        List<LifeTagRouteRecord> GetLifeTagRoutes(string tagId);
        List<LifeTagExampleRecord> GetLifeTagExamples(string tagId);
        List<FactSliceRecord> GetFactCandidates(IEnumerable<string> tagIds, int take);
        List<CognitionSliceRecord> GetCognitionCandidates(IEnumerable<string> tagIds, int take);
        List<CognitionCueRecord> GetCognitionCues(string cognitionId);
        Dictionary<string, List<string>> GetFactTagIds(IEnumerable<string> factIds);
        Dictionary<string, List<string>> GetCognitionTagIds(IEnumerable<string> cognitionIds);
        List<CognitionCueRecallData> FindCognitionsByCue(string text, int take);
        bool LoadPluginEnabled(string pluginId, bool defaultValue);
        void SavePluginEnabled(string pluginId, bool enabled);
        string LoadPluginDocument(string pluginId, string documentKey);
        void SavePluginDocument(string pluginId, string documentKey, string json);
        InnerRuntimeData LoadOrCreateInnerRuntime(string conversationId);
        void SaveInnerRuntime(InnerRuntimeData nextRuntime);
        PairIdentity LoadPairIdentity();
        PairIdentity SavePairIdentity(string username, string assname, string callName);
        List<IdentityCardRecord> LoadIdentityCards(string conversationId);
        IdentityCardRecord SaveIdentityCard(string conversationId, string slot, string body, string sourceMomentId);
        List<IdentityCardRecord> ApplyIdentityReview(
            string conversationId, string sourceMomentId, IdentityReviewOutputData output);
        List<MomentRecord> GetMomentsSince(string conversationId, long fromUnixMs, int take);
        BasePersonalityRecord LoadOrCreateBasePersonality(string conversationId);
        BasePersonalityRecord SaveBasePersonality(string conversationId, string narrative);
        MemoryObservationCommitData CommitMemoryObservation(
            MomentRecord sourceMoment,
            string subagentId,
            MemoryObservationOutputData output,
            IEnumerable<string> allowedCandidateTagIds);
        List<CognitionSliceRecord> CommitCognitions(
            string triggerMomentId,
            IEnumerable<BrainCognitionWriteData> mutations);
        void SaveTurnReview(TurnReviewRecord review);
        List<LadderItemRecord> GetAllLadderItems();
        List<EventIndexRecord> GetActiveEventIndexes();
        List<EventEntryRecord> GetEventEntriesByIndexIds(IEnumerable<string> indexIds);
        EventDimensionValuesData GetEventIndexDimensionValues();
        List<EventIndexRecord> GetEventIndexesByFilter(
            IEnumerable<string> conceptIds,
            IEnumerable<string> timeLabels,
            IEnumerable<string> dayKinds,
            IEnumerable<string> placeLabels,
            IEnumerable<string> personLabels,
            IEnumerable<string> moodLabels,
            IEnumerable<string> monthBuckets,
            int limit);
        void SaveEventIndex(EventIndexRecord index);
        void AppendEventEntry(EventEntryRecord entry);
        int MarkMomentsBuilt(IEnumerable<string> momentIds);
        DayTrajectoryRecord LoadDayTrajectory(string dayKey);
        void SaveDayTrajectory(string dayKey, string text);
        List<TodayNewItemRecord> GetTodayNewItems(string conversationId, long fromUnixMs, int take);
        int AddTodayNewItems(
            string conversationId,
            IEnumerable<string> contents,
            string sourceMomentId,
            string dayKey,
            long nowUnixMs);
    }
}
