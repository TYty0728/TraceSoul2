using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLite;
using TraceSoul2.Data;

namespace TraceSoul2.Migrate
{
    /// <summary>迁移专用库：游标、复盘状态、时间阶梯。另持有一个主库只读/直改连接做范围查询。</summary>
    public sealed class MigrationDb : IDisposable
    {
        private readonly SQLiteConnection connection;
        private readonly SQLiteConnection brain;
        private readonly object brainWriteGate = new object();

        public MigrationDb(string migrationDbPath, string brainframePath)
        {
            var dir = Path.GetDirectoryName(migrationDbPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            connection = new SQLiteConnection(migrationDbPath);
            connection.EnableWriteAheadLogging();
            connection.CreateTable<ImportCursorRecord>();
            connection.CreateTable<ReviewStateRecord>();
            connection.CreateTable<ReplayCallLogRecord>();
            brain = new SQLiteConnection(brainframePath);
        }

        // ---------- 导入游标 ----------

        public ImportCursorRecord GetCursor(string sourceFile)
        {
            return connection.Find<ImportCursorRecord>(sourceFile);
        }

        public void SaveCursor(ImportCursorRecord cursor)
        {
            cursor.UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            connection.InsertOrReplace(cursor);
        }

        // ---------- 复盘状态 ----------

        public ReviewStateRecord GetReviewState(string dayKey)
        {
            return connection.Find<ReviewStateRecord>(dayKey);
        }

        public void SaveReviewState(ReviewStateRecord state)
        {
            state.UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            connection.InsertOrReplace(state);
        }

        /// <summary>某天是否已完整跑过当天循环（构筑+复盘+日榜），用独立的完成标记，不受榜单晋升移动影响。</summary>
        public bool IsDayCompleted(string dayKey)
        {
            var state = connection.Find<ReviewStateRecord>(dayKey);
            return state != null && string.Equals(state.Status, "done", StringComparison.OrdinalIgnoreCase);
        }

        public void MarkDayCompleted(string dayKey)
        {
            connection.InsertOrReplace(new ReviewStateRecord
            {
                DayKey = dayKey,
                Status = "done",
                UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        public List<string> GetDoneDayKeys()
        {
            return connection.Table<ReviewStateRecord>()
                .Where(x => x.Status == "done")
                .Select(x => x.DayKey)
                .ToList();
        }

        // ---------- 时间阶梯（存主库 brainframe，供活体系统挂载注入） ----------

        public void ReplaceLadder(string tier, string periodKey, List<LadderItemRecord> items)
        {
            lock (brainWriteGate)
                brain.RunInTransaction(() =>
                {
                    brain.Execute(
                        "DELETE FROM ladder_items WHERE Tier=? AND PeriodKey=?", tier, periodKey);
                    var rank = 0;
                    foreach (var item in items ?? new List<LadderItemRecord>())
                    {
                        rank += 1;
                        item.Id = tier + "|" + periodKey + "|" + item.ListKind + "|" + rank;
                        item.Tier = tier;
                        item.PeriodKey = periodKey;
                        item.Rank = rank;
                        item.CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        brain.Insert(item);
                    }
                });
        }

        public List<LadderItemRecord> GetLadder(string tier, string periodKey)
        {
            return brain.Table<LadderItemRecord>()
                .Where(x => x.Tier == tier && x.PeriodKey == periodKey)
                .OrderBy(x => x.ListKind)
                .ThenBy(x => x.Rank)
                .ToList();
        }

        public List<LadderItemRecord> GetLadderAll(string tier)
        {
            return brain.Table<LadderItemRecord>()
                .Where(x => x.Tier == tier)
                .OrderBy(x => x.PeriodKey)
                .ThenBy(x => x.ListKind)
                .ThenBy(x => x.Rank)
                .ToList();
        }

        public List<ReviewStateRecord> GetAllReviewStates()
        {
            return connection.Table<ReviewStateRecord>()
                .OrderBy(x => x.DayKey)
                .ToList();
        }

        // ---------- 主库直查（只读为主，Realm 修正除外） ----------

        public int CountImportedMomentsInRange(long startMs, long endMs)
        {
            return brain.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM moments WHERE SourcePluginId=? AND CreatedUnixMs>=? AND CreatedUnixMs<?",
                MigrationContext.ImportPluginId, startMs, endMs);
        }

        public void DeleteImportedMomentsInRange(long startMs, long endMs)
        {
            brain.Execute(
                "DELETE FROM moments WHERE SourcePluginId=? AND CreatedUnixMs>=? AND CreatedUnixMs<?",
                MigrationContext.ImportPluginId, startMs, endMs);
        }

        public List<MomentRecord> GetImportedMomentsInRange(long startMs, long endMs)
        {
            return brain.Query<MomentRecord>(
                "SELECT * FROM moments WHERE SourcePluginId=? AND CreatedUnixMs>=? AND CreatedUnixMs<? ORDER BY CreatedUnixMs",
                MigrationContext.ImportPluginId, startMs, endMs);
        }

        /// <summary>
        /// 当天尚未归档的 Moment（不限来源：老日志导入的与实时对话保存的都算）。
        /// 日复盘兜底消费这里；实时归档会先把 Moment 标成 built，就不会再进来。
        /// </summary>
        public List<MomentRecord> GetUnbuiltMomentsInRange(long startMs, long endMs)
        {
            return brain.Query<MomentRecord>(
                "SELECT * FROM moments WHERE CreatedUnixMs>=? AND CreatedUnixMs<? " +
                "AND (MemoryStatus IS NULL OR MemoryStatus NOT IN ('built','operational')) ORDER BY CreatedUnixMs",
                startMs, endMs);
        }

        /// <summary>范围内的记忆日（04:00 边界）列表，升序。</summary>
        public List<string> GetMemoryDaysInRange(long startMs, long endMs)
        {
            return brain.QueryScalars<string>(
                "SELECT DISTINCT strftime('%Y-%m-%d', datetime(CreatedUnixMs/1000, 'unixepoch', '+8 hours', '-4 hours')) " +
                "FROM moments WHERE CreatedUnixMs>=? AND CreatedUnixMs<? " +
                "AND (MemoryStatus IS NULL OR MemoryStatus!='operational') ORDER BY 1",
                startMs, endMs);
        }

        public long MinMomentUnixMs()
        {
            return brain.ExecuteScalar<long>("SELECT COALESCE(MIN(CreatedUnixMs), 0) FROM moments");
        }

        public long MaxMomentUnixMs()
        {
            return brain.ExecuteScalar<long>("SELECT COALESCE(MAX(CreatedUnixMs), 0) FROM moments");
        }

        public int CountDayLadder(string periodKey)
        {
            return brain.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM ladder_items WHERE Tier='day' AND PeriodKey=?", periodKey);
        }

        /// <summary>已晋升到上层榜单的事件 ID 集合（日榜重排时排除，保证跨层不重复）。</summary>
        public HashSet<string> GetPromotedRefIds()
        {
            return new HashSet<string>(
                brain.QueryScalars<string>("SELECT DISTINCT RefId FROM ladder_items WHERE Tier!='day'"),
                StringComparer.Ordinal);
        }

        /// <summary>把给定 RefId 从某层某批周期里移除（晋升=移动：从下级空出）。</summary>
        public void RemoveFromLadder(string tier, IEnumerable<string> periodKeys, IEnumerable<string> refIds)
        {
            var periods = (periodKeys ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            var refs = (refIds ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            if (periods.Count == 0 || refs.Count == 0) return;
            lock (brainWriteGate)
                brain.RunInTransaction(() =>
                {
                    foreach (var refId in refs)
                        foreach (var period in periods)
                            brain.Execute(
                                "DELETE FROM ladder_items WHERE Tier=? AND PeriodKey=? AND RefId=?",
                                tier, period, refId);
                });
        }

        /// <summary>
        /// 清理同一 RefId 同时出现在多个阶梯层级的旧数据，只保留最高层级。
        /// 同层的不同周期记录不在此处处理。
        /// </summary>
        public int PruneCrossTierLadderDuplicates()
        {
            lock (brainWriteGate)
                return brain.Execute(
                    "DELETE FROM ladder_items " +
                    "WHERE EXISTS (" +
                    "SELECT 1 FROM ladder_items higher " +
                    "WHERE higher.RefId=ladder_items.RefId AND " +
                    "(CASE higher.Tier WHEN 'day' THEN 1 WHEN 'week' THEN 2 WHEN 'month' THEN 3 WHEN 'year' THEN 4 WHEN 'forever' THEN 5 ELSE 0 END) > " +
                    "(CASE ladder_items.Tier WHEN 'day' THEN 1 WHEN 'week' THEN 2 WHEN 'month' THEN 3 WHEN 'year' THEN 4 WHEN 'forever' THEN 5 ELSE 0 END)" +
                    ")");
        }

        /// <summary>把范围内的 Moment 标记为已归档（built），返回更新条数。</summary>
        public int MarkMomentsBuiltByRange(long startMs, long endMs)
        {
            lock (brainWriteGate)
                return brain.Execute(
                    "UPDATE moments SET MemoryStatus='built' WHERE CreatedUnixMs>=? AND CreatedUnixMs<? " +
                    "AND (MemoryStatus IS NULL OR MemoryStatus NOT IN ('built','operational'))",
                    startMs, endMs);
        }

        public List<MomentRecord> GetUnclassifiedMomentsInRange(long startMs, long endMs, int take)
        {
            return brain.Query<MomentRecord>(
                "SELECT * FROM moments WHERE SourcePluginId=? AND Realm='unclassified' AND CreatedUnixMs>=? AND CreatedUnixMs<? ORDER BY CreatedUnixMs LIMIT ?",
                MigrationContext.ImportPluginId, startMs, endMs, take);
        }

        public void UpdateMomentRealm(string momentId, string realm, string evidenceType)
        {
            lock (brainWriteGate)
                brain.Execute("UPDATE moments SET Realm=?, EvidenceType=? WHERE Id=?", realm, evidenceType, momentId);
        }

        /// <summary>确定性兜底：把（表情）/［图片］这类括号占位符归为 meta，返回更新条数。</summary>
        public int ApplyPlaceholderRealmFallback()
        {
            var updated = 0;
            lock (brainWriteGate)
            {
                var rows = brain.Query<MomentRecord>(
                    "SELECT * FROM moments WHERE Realm='unclassified'");
                foreach (var moment in rows)
                {
                    var content = (moment.Content ?? string.Empty).Trim();
                    var placeholder = (content.StartsWith("（") && content.EndsWith("）") && content.Length <= 12)
                        || (content.StartsWith("[") && content.EndsWith("]") && content.Length <= 12);
                    if (!placeholder) continue;
                    brain.Execute("UPDATE moments SET Realm=?, EvidenceType=? WHERE Id=?",
                        TraceRealmValues.Meta, EvidenceTypeValues.PluginObserved, moment.Id);
                    updated += 1;
                }
            }
            return updated;
        }

        public List<FactSliceRecord> GetAllActiveFacts()
        {
            return brain.Query<FactSliceRecord>("SELECT * FROM fact_slices WHERE Status='active'");
        }

        public List<CognitionSliceRecord> GetAllActiveCognitions()
        {
            return brain.Query<CognitionSliceRecord>("SELECT * FROM cognition_slices WHERE Status='active'");
        }

        /// <summary>某时间范围内创建的 active 认知（认知日榜候选）。</summary>
        public List<CognitionSliceRecord> GetCognitionsCreatedInRange(long startMs, long endMs)
        {
            return brain.Query<CognitionSliceRecord>(
                "SELECT * FROM cognition_slices WHERE CreatedUnixMs>=? AND CreatedUnixMs<? AND Status='active' ORDER BY CreatedUnixMs",
                startMs, endMs);
        }

        public List<CognitionEvidenceRecord> GetAllCognitionEvidence()
        {
            return brain.Query<CognitionEvidenceRecord>("SELECT * FROM cognition_evidence");
        }

        public List<MemoryObservationRunRecord> GetObservationRuns(IEnumerable<string> momentIds)
        {
            var wanted = new HashSet<string>(momentIds ?? Enumerable.Empty<string>());
            if (wanted.Count == 0) return new List<MemoryObservationRunRecord>();
            return brain.Table<MemoryObservationRunRecord>().ToList()
                .Where(x => wanted.Contains(x.MomentId))
                .OrderBy(x => x.CreatedUnixMs)
                .ToList();
        }

        public List<LifeTagRecord> GetTagsBySourceMomentIds(IEnumerable<string> momentIds)
        {
            var wanted = new HashSet<string>(momentIds ?? Enumerable.Empty<string>());
            if (wanted.Count == 0) return new List<LifeTagRecord>();
            return brain.Table<LifeTagRecord>().ToList()
                .Where(x => wanted.Contains(x.SourceMomentId))
                .OrderBy(x => x.CreatedUnixMs)
                .ToList();
        }

        public void SaveCallLog(ReplayCallLogRecord record)
        {
            record.CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            connection.Insert(record);
        }

        public List<ReplayCallLogRecord> GetCallLogs(string dayKey)
        {
            return connection.Table<ReplayCallLogRecord>()
                .Where(x => x.DayKey == dayKey)
                .OrderBy(x => x.CreatedUnixMs)
                .ToList();
        }

        /// <summary>单天强制重放前的清理：删除该天产出的记忆层（认知保留，由人工审核处理重复）。</summary>
        public void DeleteDayMemoryArtifacts(IEnumerable<string> momentIds, string dayKey)
        {
            var wanted = new HashSet<string>(momentIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            if (wanted.Count == 0) return;
            brain.RunInTransaction(() =>
            {
                var factIds = brain.Table<FactSliceRecord>().ToList()
                    .Where(x => wanted.Contains(x.SourceMomentId))
                    .Select(x => x.Id).ToList();
                foreach (var factId in factIds)
                {
                    brain.Execute("DELETE FROM fact_tag_links WHERE FactId=?", factId);
                    brain.Execute("DELETE FROM fact_wakes WHERE FactId=?", factId);
                    brain.Execute("DELETE FROM fact_slices WHERE Id=?", factId);
                }
                foreach (var momentId in wanted)
                {
                    brain.Execute("DELETE FROM fact_wakes WHERE TriggerMomentId=?", momentId);
                    brain.Execute("DELETE FROM memory_observation_runs WHERE MomentId=?", momentId);
                }
            });
            connection.RunInTransaction(() =>
            {
                connection.Execute("DELETE FROM migration_review_state WHERE DayKey=?", dayKey);
                connection.Execute("DELETE FROM replay_call_log WHERE DayKey=?", dayKey);
            });
            brain.Execute("DELETE FROM ladder_items WHERE Tier='day' AND PeriodKey=?", dayKey);
        }

        /// <summary>把重复认知合并进保留项：停用重复项，证据/链接/线索/边/阶梯指针全部重指向保留项。</summary>
        public void MergeCognitionInto(string duplicateId, string keptId, string keptSummary)
        {
            lock (brainWriteGate)
            {
                brain.RunInTransaction(() =>
                {
                    var kept = brain.Find<CognitionSliceRecord>(keptId);
                    var dup = brain.Find<CognitionSliceRecord>(duplicateId);
                    if (kept == null || dup == null || keptId == duplicateId) return;
                    kept.Confidence = Math.Max(kept.Confidence, dup.Confidence);
                    kept.UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    brain.Update(kept);

                    foreach (var link in brain.Table<CognitionTagLinkRecord>()
                                 .Where(x => x.CognitionId == duplicateId).ToList())
                    {
                        var id = keptId + "|" + link.TagId;
                        if (brain.Find<CognitionTagLinkRecord>(id) == null)
                            brain.Insert(new CognitionTagLinkRecord
                            {
                                Id = id,
                                CognitionId = keptId,
                                TagId = link.TagId,
                                Weight = link.Weight
                            });
                        brain.Delete(link);
                    }
                    brain.Execute("UPDATE cognition_evidence SET CognitionId=? WHERE CognitionId=?", keptId, duplicateId);
                    brain.Execute("UPDATE cognition_cues SET CognitionId=? WHERE CognitionId=?", keptId, duplicateId);
                    brain.Execute("UPDATE cognition_edges SET FromCognitionId=? WHERE FromCognitionId=?", keptId, duplicateId);
                    brain.Execute("UPDATE cognition_edges SET ToCognitionId=? WHERE ToCognitionId=?", keptId, duplicateId);
                    brain.Execute("UPDATE ladder_items SET RefId=?, Label=? WHERE RefId=? AND RefKind='cognition'", keptId, keptSummary, duplicateId);
                    dup.Status = "inactive";
                    dup.UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    brain.Update(dup);
                });
            }
        }

        /// <summary>删除阶梯里指向同一 Ref 的重复条目（认知合并后同一认知可能出现多条指针），每组按最小 Id 保留。</summary>
        public void RemoveDuplicateLadderRefs()
        {
            lock (brainWriteGate)
                brain.RunInTransaction(() =>
                {
                    var dups = brain.Query<LadderItemRecord>(
                        "SELECT * FROM ladder_items WHERE Id NOT IN (" +
                        "SELECT MIN(Id) FROM ladder_items GROUP BY Tier, PeriodKey, ListKind, RefId)");
                    foreach (var item in dups)
                        brain.Delete(item);
                });
        }

        public List<ReplayCallLogRecord> GetCallLogsRecent(int take)
        {
            return connection.Table<ReplayCallLogRecord>()
                .OrderByDescending(x => x.CreatedUnixMs)
                .Take(Math.Max(1, take))
                .ToList();
        }

        public int CountMoments()
        {
            return brain.ExecuteScalar<int>("SELECT COUNT(*) FROM moments");
        }

        public int CountEventEntries()
        {
            return brain.ExecuteScalar<int>("SELECT COUNT(*) FROM event_entries");
        }

        public int CountSensoryTags()
        {
            return brain.ExecuteScalar<int>("SELECT COUNT(*) FROM life_tags WHERE Origin='sensory'");
        }

        // ---------- 新路线：Tag 创建/激活 + 观察留痕（不再依赖内核旧事实通道） ----------

        /// <summary>创建新 Tag（按名去重复用）+ 激活选中 Tag + 写观察留痕。返回被激活的 Tag。</summary>
        public List<LifeTagRecord> CreateAndActivateTags(
            MemoryObservationOutputData output,
            MomentRecord sourceMoment,
            List<string> allowedTagIds,
            PairIdentity pair)
        {
            var createdByName = new Dictionary<string, LifeTagRecord>(StringComparer.Ordinal);
            var selected = new List<LifeTagRecord>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var allowed = new HashSet<string>(allowedTagIds ?? new List<string>(), StringComparer.Ordinal);
            lock (brainWriteGate)
            {
                brain.RunInTransaction(() =>
                {
                    foreach (var proposal in output.new_tags ?? new List<NewLifeTagWriteData>())
                    {
                        if (proposal == null) continue;
                        var label = Limit(pair.RewriteRecordedText((proposal.name ?? string.Empty).Trim()), 30);
                        var definition = Limit(pair.RewriteRecordedText((proposal.definition ?? string.Empty).Trim()), 240);
                        if (label.Length == 0 || definition.Length == 0) continue;
                        var domainRoutes = (proposal.domain_ids ?? new List<string>())
                            .Select(pair.CanonicalDomain)
                            .Where(x => !string.IsNullOrEmpty(x))
                            .Distinct()
                            .Select(x => "domain." + x)
                            .Take(4)
                            .ToList();
                        var dimensionRoutes = (proposal.dimension_ids ?? new List<string>())
                            .Select(x => x != null && x.StartsWith("dimension.") ? x : "dimension." + (x ?? string.Empty))
                            .Where(x => x.Length > "dimension.".Length &&
                                        LifeRouteValues.IsDimension(x.Substring("dimension.".Length)))
                            .Distinct()
                            .Take(8)
                            .ToList();
                        if (domainRoutes.Count == 0 || dimensionRoutes.Count == 0) continue;
                        var existing = brain.Table<LifeTagRecord>().FirstOrDefault(x => x.Label == label);
                        if (existing != null)
                        {
                            createdByName[label] = existing;
                            continue;
                        }
                        var tag = new LifeTagRecord
                        {
                            Id = "concept.life." + Guid.NewGuid().ToString("N"),
                            Label = label,
                            Definition = definition,
                            Status = "active",
                            Origin = "sensory",
                            SourceMomentId = sourceMoment.Id,
                            ActivationCount = 0,
                            CreatedUnixMs = now,
                            UpdatedUnixMs = now
                        };
                        brain.Insert(tag);
                        foreach (var route in domainRoutes)
                            brain.Insert(new LifeTagRouteRecord
                            {
                                Id = tag.Id + "|" + route,
                                TagId = tag.Id,
                                RouteNodeId = route,
                                RouteLevel = "domain",
                                Weight = 1f
                            });
                        foreach (var route in dimensionRoutes)
                            brain.Insert(new LifeTagRouteRecord
                            {
                                Id = tag.Id + "|" + route,
                                TagId = tag.Id,
                                RouteNodeId = route,
                                RouteLevel = "dimension",
                                Weight = 1f
                            });
                        var positiveIndex = 0;
                        foreach (var example in (proposal.positive_examples ?? new List<string>()).Take(6))
                        {
                            var text = Limit(pair.RewriteRecordedText(example), 100);
                            if (text.Length == 0) continue;
                            brain.Insert(new LifeTagExampleRecord
                            {
                                Id = tag.Id + "|positive|" + positiveIndex,
                                TagId = tag.Id,
                                Role = "positive",
                                Text = text,
                                ExampleIndex = positiveIndex
                            });
                            positiveIndex += 1;
                        }
                        var negativeIndex = 0;
                        foreach (var example in (proposal.negative_examples ?? new List<string>()).Take(6))
                        {
                            var text = Limit(pair.RewriteRecordedText(example), 100);
                            if (text.Length == 0) continue;
                            brain.Insert(new LifeTagExampleRecord
                            {
                                Id = tag.Id + "|negative|" + negativeIndex,
                                TagId = tag.Id,
                                Role = "negative",
                                Text = text,
                                ExampleIndex = negativeIndex
                            });
                            negativeIndex += 1;
                        }
                        createdByName[label] = tag;
                    }

                    var selectedIds = new HashSet<string>(
                        output.selected_tag_ids ?? new List<string>(), StringComparer.Ordinal);
                    foreach (var tag in createdByName.Values) selectedIds.Add(tag.Id);
                    foreach (var id in selectedIds.Where(allowed.Contains).Take(8))
                    {
                        var tag = brain.Find<LifeTagRecord>(id);
                        if (tag == null || tag.Status != "active") continue;
                        tag.ActivationCount += 1;
                        tag.UpdatedUnixMs = now;
                        brain.Update(tag);
                        selected.Add(tag);
                    }

                    brain.Insert(new MemoryObservationRunRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        MomentId = sourceMoment.Id,
                        ObserverId = "legacy.event.observer",
                        PerceptionSummary = Limit(output.perception_summary ?? string.Empty, 300),
                        FactDecision = Limit(output.fact_decision ?? string.Empty, 300),
                        CreatedUnixMs = now
                    });
                });
            }
            return selected;
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        // ---------- 第四层：多维索引与追加条目 ----------

        public void SaveEventIndex(EventIndexRecord index)
        {
            lock (brainWriteGate)
                brain.Insert(index);
        }

        public List<EventIndexRecord> GetActiveEventIndexes()
        {
            lock (brainWriteGate)
                return brain.Table<EventIndexRecord>()
                    .Where(x => x.Status == "active")
                    .OrderByDescending(x => x.UpdatedUnixMs)
                    .ToList();
        }

        /// <summary>有事件的全部记忆日（按 +08:00 归日，正序），认知回填逐天迭代用。</summary>
        public List<string> GetDistinctEventDays()
        {
            return brain.QueryScalars<string>(
                "SELECT DISTINCT date(TimeUnixMs/1000,'unixepoch','+8 hours') FROM event_indexes WHERE Status='active' ORDER BY 1");
        }

        public List<EventIndexRecord> GetEventIndexCandidates(List<string> tagIds, int take)
        {
            if (take <= 0) return new List<EventIndexRecord>();
            var wanted = new HashSet<string>(tagIds ?? new List<string>(), StringComparer.Ordinal);
            if (wanted.Count == 0) return new List<EventIndexRecord>();
            return GetActiveEventIndexes()
                .Where(x => (x.TagIds ?? string.Empty)
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(wanted.Contains))
                .Take(Math.Min(20, take))
                .ToList();
        }

        public void AppendEventEntry(EventEntryRecord entry)
        {
            lock (brainWriteGate)
                brain.Insert(entry);
        }

        public void UpdateEventEntryDetail(string entryId, string detail)
        {
            lock (brainWriteGate)
                brain.Execute("UPDATE event_entries SET Detail=? WHERE Id=?", detail, entryId);
        }

        public List<EventEntryRecord> GetEntriesByIndexIds(List<string> indexIds)
        {
            var wanted = new HashSet<string>(indexIds ?? new List<string>(), StringComparer.Ordinal);
            if (wanted.Count == 0) return new List<EventEntryRecord>();
            lock (brainWriteGate)
                return brain.Table<EventEntryRecord>()
                    .Where(x => wanted.Contains(x.IndexId))
                    .OrderBy(x => x.CreatedUnixMs)
                    .ToList();
        }

        /// <summary>整批替换某天的日榜（幂等：重跑当天只保留最新一版）。</summary>
        public void ReplaceDayLadder(string periodKey, List<LadderItemRecord> items)
        {
            ReplaceLadder("day", periodKey, items);
        }

        /// <summary>读取某层在给定周期列表里的全部榜单条目（按 Rank 升序）。</summary>
        public List<LadderItemRecord> GetLadderItems(string tier, IEnumerable<string> periodKeys)
        {
            var wanted = new HashSet<string>(periodKeys ?? new List<string>(), StringComparer.Ordinal);
            if (wanted.Count == 0) return new List<LadderItemRecord>();
            return brain.Table<LadderItemRecord>()
                .Where(x => x.Tier == tier && wanted.Contains(x.PeriodKey))
                .OrderBy(x => x.Rank)
                .ToList();
        }

        /// <summary>某层全部周期键（去重升序）。</summary>
        public List<string> GetLadderPeriodKeys(string tier)
        {
            return brain.QueryScalars<string>(
                "SELECT DISTINCT PeriodKey FROM ladder_items WHERE Tier=? ORDER BY 1", tier);
        }

        public void Dispose()
        {
            brain.Dispose();
            connection.Dispose();
        }
    }

    [Table("import_cursors")]
    public sealed class ImportCursorRecord
    {
        [PrimaryKey]
        public string SourceFile { get; set; }
        public long LastLine { get; set; }
        public string LastTimestamp { get; set; }
        public long FileSize { get; set; }
        public string LastLineHash { get; set; }
        public string Status { get; set; }
        public long UpdatedUnixMs { get; set; }
    }

    [Table("migration_review_state")]
    public sealed class ReviewStateRecord
    {
        [PrimaryKey]
        public string DayKey { get; set; }
        public string Status { get; set; }
        public string LastMomentId { get; set; }
        public int MomentCount { get; set; }
        public int ObservationCalls { get; set; }
        public int FactCount { get; set; }
        public int CognitionCount { get; set; }
        public int TagCount { get; set; }
        public string Error { get; set; }
        public long UpdatedUnixMs { get; set; }
    }

    [Table("replay_call_log")]
    public sealed class ReplayCallLogRecord
    {
        [PrimaryKey]
        public string Id { get; set; }
        public string DayKey { get; set; }
        public string CallKind { get; set; }
        public int ChunkIndex { get; set; }
        public string OutputJson { get; set; }
        public string Error { get; set; }
        public long CreatedUnixMs { get; set; }
    }
}
