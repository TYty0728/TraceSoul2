using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SQLite;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Util;

namespace TraceSoul2.Manager
{
    /// <summary>
    /// 全新的 TraceSoul2 持久层。事实与认知分表，写入权限由公开方法明确隔离。
    /// </summary>
    public sealed class SqliteMemoryManager : IMemoryStore, IDisposable
    {
        private readonly SQLiteConnection connection;

        public string DatabasePath { get; private set; }

        /// <summary>事实/认知候选的最近 N 条窗口。迁移复盘等大数据场景可调大，防止旧事实被挤出候选。</summary>
        public int CandidateWindow { get; set; } = 500;

        public SqliteMemoryManager(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path is required.", "databasePath");
            DatabasePath = databasePath;
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            connection = new SQLiteConnection(databasePath);
            try
            {
                connection.EnableWriteAheadLogging();
                InitializeSchema();
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public void SaveMoment(MomentRecord moment)
        {
            if (moment == null) throw new ArgumentNullException("moment");
            var pair = LoadPairIdentity();
            if (pair.IsComplete) moment.Role = pair.CanonicalMomentRole(moment.Role);
            connection.Insert(moment);
        }

        public void SaveOperationalEvent(OperationalEventRecord operationalEvent)
        {
            if (operationalEvent == null) throw new ArgumentNullException("operationalEvent");
            var pair = LoadPairIdentity();
            if (pair.IsComplete)
                operationalEvent.Role = pair.CanonicalMomentRole(operationalEvent.Role);
            connection.Insert(operationalEvent);
        }

        public List<MomentRecord> GetRecentMoments(string conversationId, int take)
        {
            if (take <= 0) return new List<MomentRecord>();
            return connection.Table<MomentRecord>()
                .Where(x => x.ConversationId == conversationId &&
                            (x.MemoryStatus == null || x.MemoryStatus != "operational"))
                .OrderByDescending(x => x.CreatedUnixMs)
                .Take(Math.Min(200, take))
                .ToList()
                .OrderBy(x => x.CreatedUnixMs)
                .ToList();
        }

        public List<OperationalEventRecord> GetRecentOperationalEvents(string conversationId, int take)
        {
            if (take <= 0) return new List<OperationalEventRecord>();
            return connection.Table<OperationalEventRecord>()
                .Where(x => x.ConversationId == conversationId)
                .OrderByDescending(x => x.OccurredUnixMs)
                .Take(Math.Min(200, take))
                .ToList()
                .OrderBy(x => x.OccurredUnixMs)
                .ToList();
        }

        public List<TurnReviewRecord> GetRecentTurnReviews(string conversationId, int take)
        {
            if (take <= 0) return new List<TurnReviewRecord>();
            return connection.Table<TurnReviewRecord>()
                .Where(x => x.ConversationId == conversationId)
                .OrderByDescending(x => x.CreatedUnixMs)
                .Take(Math.Min(50, take))
                .ToList()
                .OrderBy(x => x.CreatedUnixMs)
                .ToList();
        }

        public void SeedLifeTags(IEnumerable<VectorIndexNode> ontology)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            connection.RunInTransaction(() =>
            {
                foreach (var node in ontology ?? Enumerable.Empty<VectorIndexNode>())
                {
                    if (node.Level != VectorNodeLevel.Concept) continue;
                    var existing = connection.Find<LifeTagRecord>(node.Id);
                    if (existing == null)
                    {
                        connection.Insert(new LifeTagRecord
                        {
                            Id = node.Id,
                            Label = node.Label,
                            Definition = node.Definition,
                            Status = "active",
                            Origin = "seed",
                            SourceMomentId = string.Empty,
                            ActivationCount = 0,
                            CreatedUnixMs = now,
                            UpdatedUnixMs = now
                        });
                    }
                    else if (existing.Origin == "seed" &&
                             (existing.Label != node.Label || existing.Definition != node.Definition))
                    {
                        existing.Label = node.Label;
                        existing.Definition = node.Definition;
                        existing.UpdatedUnixMs = now;
                        connection.Update(existing);
                    }

                    UpsertSeedRoute(node.Id, "dimension." + node.DimensionKey, "dimension", 1f);
                    foreach (var domain in node.ApplicableDomains)
                        UpsertSeedRoute(node.Id, "domain." + domain, "domain", 1f);
                    SeedExamples(node);
                }
            });
        }

        public List<LifeTagRecord> GetActiveLifeTags()
        {
            return connection.Table<LifeTagRecord>()
                .Where(x => x.Status == "active")
                .OrderBy(x => x.Label)
                .ToList();
        }

        public List<LifeTagRouteRecord> GetLifeTagRoutes(string tagId)
        {
            if (string.IsNullOrWhiteSpace(tagId)) return new List<LifeTagRouteRecord>();
            return connection.Table<LifeTagRouteRecord>()
                .Where(x => x.TagId == tagId)
                .ToList();
        }

        public List<LifeTagExampleRecord> GetLifeTagExamples(string tagId)
        {
            if (string.IsNullOrWhiteSpace(tagId)) return new List<LifeTagExampleRecord>();
            return connection.Table<LifeTagExampleRecord>()
                .Where(x => x.TagId == tagId)
                .OrderBy(x => x.Role)
                .ThenBy(x => x.ExampleIndex)
                .ToList();
        }

        public List<FactSliceRecord> GetFactCandidates(IEnumerable<string> tagIds, int take)
        {
            if (take <= 0) return new List<FactSliceRecord>();
            var wanted = new HashSet<string>(tagIds ?? Enumerable.Empty<string>());
            if (wanted.Count == 0) return new List<FactSliceRecord>();
            var activeFacts = connection.Table<FactSliceRecord>()
                .Where(x => x.Status == "active")
                .OrderByDescending(x => x.CreatedUnixMs)
                .Take(Math.Max(1, CandidateWindow))
                .ToList();
            var links = connection.Table<FactTagLinkRecord>().ToList();
            return activeFacts
                .Select(f => new
                {
                    Fact = f,
                    Matches = links.Count(x => x.FactId == f.Id && wanted.Contains(x.TagId))
                })
                .Where(x => x.Matches > 0)
                .OrderByDescending(x => x.Matches)
                .ThenByDescending(x => x.Fact.LastWokenUnixMs)
                .ThenByDescending(x => x.Fact.CreatedUnixMs)
                .Take(Math.Min(100, take))
                .Select(x => x.Fact)
                .ToList();
        }

        public List<CognitionSliceRecord> GetCognitionCandidates(IEnumerable<string> tagIds, int take)
        {
            if (take <= 0) return new List<CognitionSliceRecord>();
            var wanted = new HashSet<string>(tagIds ?? Enumerable.Empty<string>());
            if (wanted.Count == 0) return new List<CognitionSliceRecord>();
            var active = connection.Table<CognitionSliceRecord>()
                .Where(x => x.Status == "active")
                .OrderByDescending(x => x.UpdatedUnixMs)
                .Take(Math.Max(1, CandidateWindow))
                .ToList();
            var links = connection.Table<CognitionTagLinkRecord>().ToList();
            return active
                .Select(c => new
                {
                    Cognition = c,
                    Matches = links.Count(x => x.CognitionId == c.Id && wanted.Contains(x.TagId))
                })
                .Where(x => x.Matches > 0)
                .OrderByDescending(x => x.Matches)
                .ThenByDescending(x => x.Cognition.UpdatedUnixMs)
                .Take(Math.Min(100, take))
                .Select(x => x.Cognition)
                .ToList();
        }

        public List<CognitionCueRecord> GetCognitionCues(string cognitionId)
        {
            if (string.IsNullOrWhiteSpace(cognitionId)) return new List<CognitionCueRecord>();
            return connection.Table<CognitionCueRecord>()
                .Where(x => x.CognitionId == cognitionId)
                .OrderByDescending(x => x.AssociationStrength)
                .ToList();
        }

        public Dictionary<string, List<string>> GetFactTagIds(IEnumerable<string> factIds)
        {
            var wanted = new HashSet<string>(factIds ?? Enumerable.Empty<string>());
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0) return result;
            foreach (var link in connection.Table<FactTagLinkRecord>())
            {
                if (!wanted.Contains(link.FactId)) continue;
                List<string> tags;
                if (!result.TryGetValue(link.FactId, out tags))
                {
                    tags = new List<string>();
                    result[link.FactId] = tags;
                }
                if (!tags.Contains(link.TagId)) tags.Add(link.TagId);
            }
            return result;
        }

        public Dictionary<string, List<string>> GetCognitionTagIds(IEnumerable<string> cognitionIds)
        {
            var wanted = new HashSet<string>(cognitionIds ?? Enumerable.Empty<string>());
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0) return result;
            foreach (var link in connection.Table<CognitionTagLinkRecord>())
            {
                if (!wanted.Contains(link.CognitionId)) continue;
                List<string> tags;
                if (!result.TryGetValue(link.CognitionId, out tags))
                {
                    tags = new List<string>();
                    result[link.CognitionId] = tags;
                }
                if (!tags.Contains(link.TagId)) tags.Add(link.TagId);
            }
            return result;
        }

        public List<CognitionCueRecallData> FindCognitionsByCue(string text, int take)
        {
            var hits = new List<CognitionCueRecallData>();
            if (string.IsNullOrWhiteSpace(text) || take <= 0) return hits;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cue in connection.Table<CognitionCueRecord>()
                         .OrderByDescending(x => x.AssociationStrength))
            {
                var token = (cue.Cue ?? string.Empty).Trim();
                if (token.Length < 2) continue;
                if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!seen.Add(cue.CognitionId)) continue;
                var cognition = connection.Find<CognitionSliceRecord>(cue.CognitionId);
                if (cognition == null || cognition.Status != "active") continue;
                hits.Add(new CognitionCueRecallData
                {
                    Cognition = cognition,
                    Cue = token,
                    AssociationStrength = cue.AssociationStrength
                });
                if (hits.Count == Math.Min(20, take)) break;
            }
            return hits;
        }

        public bool LoadPluginEnabled(string pluginId, bool defaultValue)
        {
            pluginId = Required(pluginId, "pluginId");
            var state = connection.Find<PluginStateRecord>(pluginId);
            if (state != null) return state.Enabled;
            SavePluginEnabled(pluginId, defaultValue);
            return defaultValue;
        }

        public void SavePluginEnabled(string pluginId, bool enabled)
        {
            connection.InsertOrReplace(new PluginStateRecord
            {
                PluginId = Required(pluginId, "pluginId"),
                Enabled = enabled,
                UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        public string LoadPluginDocument(string pluginId, string documentKey)
        {
            var id = Required(pluginId, "pluginId") + ":" + Required(documentKey, "documentKey");
            var row = connection.Find<PluginDocumentRecord>(id);
            return row == null ? string.Empty : row.Json ?? string.Empty;
        }

        public void SavePluginDocument(string pluginId, string documentKey, string json)
        {
            pluginId = Required(pluginId, "pluginId");
            documentKey = Required(documentKey, "documentKey");
            connection.InsertOrReplace(new PluginDocumentRecord
            {
                Id = pluginId + ":" + documentKey,
                PluginId = pluginId,
                DocumentKey = documentKey,
                Json = json ?? string.Empty,
                UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        public InnerRuntimeData LoadOrCreateInnerRuntime(string conversationId)
        {
            conversationId = Required(conversationId, "conversationId");
            var record = connection.Find<InnerRuntimeRecord>(conversationId);
            if (record != null) return ToRuntimeData(record);
            var created = InnerLifeLogic.CreateInitial(
                conversationId,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            connection.Insert(ToRuntimeRecord(created));
            return created;
        }

        public void SaveInnerRuntime(InnerRuntimeData nextRuntime)
        {
            if (nextRuntime == null) throw new ArgumentNullException("nextRuntime");
            connection.RunInTransaction(() => WriteInnerRuntime(nextRuntime));
        }

        public List<CognitionSliceRecord> CommitCognitions(
            string triggerMomentId,
            IEnumerable<BrainCognitionWriteData> mutations)
        {
            triggerMomentId = Required(triggerMomentId, "triggerMomentId");
            var changed = new List<CognitionSliceRecord>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            connection.RunInTransaction(() =>
            {
                foreach (var mutation in mutations ?? Enumerable.Empty<BrainCognitionWriteData>())
                {
                    var cognition = ApplyCognitionMutation(mutation, triggerMomentId, now);
                    if (cognition != null) changed.Add(cognition);
                    if (changed.Count == 3) break;
                }
            });
            return changed;
        }

        public void SaveTurnReview(TurnReviewRecord review)
        {
            if (review == null) throw new ArgumentNullException("review");
            connection.Insert(review);
        }

        public List<LadderItemRecord> GetAllLadderItems()
        {
            return connection.Table<LadderItemRecord>()
                .OrderBy(x => x.Tier)
                .ThenBy(x => x.PeriodKey)
                .ThenBy(x => x.ListKind)
                .ThenBy(x => x.Rank)
                .ToList();
        }

        public List<EventIndexRecord> GetActiveEventIndexes()
        {
            return connection.Table<EventIndexRecord>()
                .Where(x => x.Status == "active")
                .OrderByDescending(x => x.UpdatedUnixMs)
                .ToList();
        }

        public List<EventEntryRecord> GetEventEntriesByIndexIds(IEnumerable<string> indexIds)
        {
            var wanted = new HashSet<string>(indexIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            if (wanted.Count == 0) return new List<EventEntryRecord>();
            return connection.Table<EventEntryRecord>()
                .Where(x => wanted.Contains(x.IndexId))
                .OrderBy(x => x.CreatedUnixMs)
                .ToList();
        }

        public void SaveEventIndex(EventIndexRecord index)
        {
            if (index == null) throw new ArgumentNullException("index");
            connection.Insert(index);
        }

        public void AppendEventEntry(EventEntryRecord entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            connection.Insert(entry);
        }

        /// <summary>把给定 moment 标记为已归档（built），返回实际更新的行数。</summary>
        public int MarkMomentsBuilt(IEnumerable<string> momentIds)
        {
            var ids = (momentIds ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            if (ids.Count == 0) return 0;
            var updated = 0;
            connection.RunInTransaction(() =>
            {
                foreach (var id in ids)
                    updated += connection.Execute(
                        "UPDATE moments SET MemoryStatus='built' WHERE Id=? AND (MemoryStatus IS NULL OR MemoryStatus!='built')", id);
            });
            return updated;
        }

        /// <summary>读取某记忆日的轨迹；顺带清掉其它日期的旧行（新的一天=清空）。</summary>
        public DayTrajectoryRecord LoadDayTrajectory(string dayKey)
        {
            connection.Execute("DELETE FROM day_trajectory WHERE DayKey!=?", dayKey);
            return connection.Find<DayTrajectoryRecord>(dayKey);
        }

        public void SaveDayTrajectory(string dayKey, string text)
        {
            connection.InsertOrReplace(new DayTrajectoryRecord
            {
                DayKey = dayKey,
                Text = text ?? string.Empty,
                UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        /// <summary>读取今日新识便签（fromUnixMs = 当日 04:00 边界起算），按时间正序。</summary>
        public List<TodayNewItemRecord> GetTodayNewItems(string conversationId, long fromUnixMs, int take)
        {
            return connection.Table<TodayNewItemRecord>()
                .Where(x => x.ConversationId == conversationId && x.CreatedUnixMs >= fromUnixMs)
                .OrderBy(x => x.CreatedUnixMs)
                .Take(Math.Max(1, Math.Min(20, take)))
                .ToList();
        }

        /// <summary>批量写入今日新识便签：同日同内容去重；返回实际新增条数。</summary>
        public int AddTodayNewItems(
            string conversationId,
            IEnumerable<string> contents,
            string sourceMomentId,
            string dayKey,
            long nowUnixMs)
        {
            var existing = new HashSet<string>(
                connection.Table<TodayNewItemRecord>()
                    .Where(x => x.ConversationId == conversationId && x.DayKey == dayKey)
                    .Select(x => x.Content),
                StringComparer.Ordinal);
            var added = 0;
            foreach (var raw in contents ?? Enumerable.Empty<string>())
            {
                var content = (raw ?? string.Empty).Trim();
                if (content.Length == 0 || content.Length > 40) continue;
                if (!existing.Add(content)) continue;
                connection.Insert(new TodayNewItemRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ConversationId = conversationId,
                    Content = content,
                    SourceMomentId = sourceMomentId ?? string.Empty,
                    DayKey = dayKey,
                    CreatedUnixMs = nowUnixMs
                });
                added += 1;
                if (added >= 10) break;
            }
            return added;
        }

        /// <summary>第四层五个交叉维度的当前全部取值（小词汇表，供记忆神经子代理选择定位）。</summary>
        public EventDimensionValuesData GetEventIndexDimensionValues()
        {
            var values = new EventDimensionValuesData();
            values.TimeLabels = connection.QueryScalars<string>(
                "SELECT DISTINCT TimeLabel FROM event_indexes WHERE Status='active' AND TimeLabel IS NOT NULL AND TimeLabel != '' ORDER BY 1");
            values.DayKindLabels = connection.QueryScalars<string>(
                "SELECT DISTINCT DayKindLabel FROM event_indexes WHERE Status='active' AND DayKindLabel IS NOT NULL AND DayKindLabel != '' ORDER BY 1");
            values.PlaceLabels = connection.QueryScalars<string>(
                "SELECT DISTINCT PlaceLabel FROM event_indexes WHERE Status='active' AND PlaceLabel IS NOT NULL AND PlaceLabel != '' ORDER BY 1");
            values.PersonLabels = connection.QueryScalars<string>(
                "SELECT DISTINCT PersonLabel FROM event_indexes WHERE Status='active' AND PersonLabel IS NOT NULL AND PersonLabel != '' ORDER BY 1");
            values.MoodLabels = connection.QueryScalars<string>(
                "SELECT DISTINCT MoodLabel FROM event_indexes WHERE Status='active' AND MoodLabel IS NOT NULL AND MoodLabel != '' ORDER BY 1");
            values.MonthBuckets = connection.QueryScalars<string>(
                "SELECT DISTINCT strftime('%Y-%m', datetime(TimeUnixMs/1000, 'unixepoch', '+8 hours')) FROM event_indexes WHERE Status='active' ORDER BY 1");
            values.TotalIndexes = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM event_indexes WHERE Status='active'");
            return values;
        }

        /// <summary>
        /// 按子代理定位过滤第四层索引：概念取并集，其余维度取并集，各维度之间取交集；
        /// 某维度集合为空表示不按该维度过滤。
        /// </summary>
        public List<EventIndexRecord> GetEventIndexesByFilter(
            IEnumerable<string> conceptIds,
            IEnumerable<string> timeLabels,
            IEnumerable<string> dayKinds,
            IEnumerable<string> placeLabels,
            IEnumerable<string> personLabels,
            IEnumerable<string> moodLabels,
            IEnumerable<string> monthBuckets,
            int limit)
        {
            var concepts = new HashSet<string>(conceptIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var conceptSuffixes = new HashSet<string>(
                concepts.Where(x => x != null && x.LastIndexOf('.') >= 0)
                    .Select(x => x.Substring(x.LastIndexOf('.') + 1))
                    .Where(x => x.Length >= 8), StringComparer.Ordinal);
            var times = new HashSet<string>(timeLabels ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var kinds = new HashSet<string>(dayKinds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var places = new HashSet<string>(placeLabels ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var persons = new HashSet<string>(personLabels ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var moods = new HashSet<string>(moodLabels ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var months = new HashSet<string>(monthBuckets ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            if (limit <= 0) limit = 500;

            var result = new List<EventIndexRecord>();
            foreach (var index in GetActiveEventIndexes())
            {
                if (concepts.Count > 0 && !MatchesConcept(index.TagIds, concepts, conceptSuffixes)) continue;
                if (times.Count > 0 && !times.Contains(index.TimeLabel ?? string.Empty)) continue;
                if (kinds.Count > 0 && !kinds.Contains(index.DayKindLabel ?? string.Empty)) continue;
                if (places.Count > 0 && !places.Contains(index.PlaceLabel ?? string.Empty)) continue;
                if (persons.Count > 0 && !persons.Contains(index.PersonLabel ?? string.Empty)) continue;
                if (moods.Count > 0 && !moods.Contains(index.MoodLabel ?? string.Empty)) continue;
                if (months.Count > 0 && !months.Contains(MonthOf(index.TimeUnixMs))) continue;
                result.Add(index);
                if (result.Count >= limit) break;
            }
            return result;
        }

        private static bool MatchesConcept(
            string tagIds, HashSet<string> concepts, HashSet<string> conceptSuffixes)
        {
            if (string.IsNullOrWhiteSpace(tagIds)) return false;
            foreach (var token in tagIds.Split(new[] { ',', ';', '，', '；', '|' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = token.Trim();
                if (trimmed.Length == 0) continue;
                if (concepts.Contains(trimmed)) return true;
                // 历史数据可能只存了 concept.life. 后的裸 GUID。
                if (conceptSuffixes.Contains(trimmed)) return true;
            }
            return false;
        }

        private static string MonthOf(long unixMs)
        {
            if (unixMs <= 0) return string.Empty;
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
                    .ToOffset(TimeSpan.FromHours(8))
                    .ToString("yyyy-MM");
            }
            catch
            {
                return string.Empty;
            }
        }

        public PairIdentity LoadPairIdentity()
        {
            var record = connection.Find<PairIdentityRecord>(PairIdentity.DefaultId);
            if (record == null) return PairIdentity.Missing;
            return PairIdentity.FromStored(record.Username, record.Assname, record.CallName);
        }

        public PairIdentity SavePairIdentity(string username, string assname, string callName)
        {
            var next = PairIdentity.Create(username, assname, callName);
            var previous = LoadPairIdentity();
            connection.RunInTransaction(() =>
            {
                connection.InsertOrReplace(new PairIdentityRecord
                {
                    Id = PairIdentity.DefaultId,
                    Username = next.Username,
                    Assname = next.Assname,
                    CallName = next.CallName,
                    UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                RewriteStoredPersonWords(previous, next);
                foreach (var card in connection.Table<IdentityCardRecord>().ToList())
                {
                    if (card.Revision != 0) continue;
                    card.Body = IdentityCardLogic.DefaultBody(card.Slot, next);
                    connection.Update(card);
                    if (card.Slot == IdentityCardSlotValues.Personality)
                        SyncBasePersonality(card.ConversationId, card.Body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
            });
            return next;
        }

        public List<MomentRecord> GetMomentsSince(string conversationId, long fromUnixMs, int take)
        {
            if (take <= 0) return new List<MomentRecord>();
            conversationId = Required(conversationId, "conversationId");
            return connection.Table<MomentRecord>()
                .Where(x => x.ConversationId == conversationId && x.CreatedUnixMs >= fromUnixMs &&
                            (x.MemoryStatus == null || x.MemoryStatus != "operational"))
                .OrderBy(x => x.CreatedUnixMs)
                .Take(Math.Min(200, take))
                .ToList();
        }

        public List<IdentityCardRecord> LoadIdentityCards(string conversationId)
        {
            conversationId = Required(conversationId, "conversationId");
            var pair = LoadPairIdentity();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var existing = connection.Table<IdentityCardRecord>()
                .Where(x => x.ConversationId == conversationId)
                .ToList();
            var result = new List<IdentityCardRecord>();
            foreach (var slot in IdentityCardSlotValues.All)
            {
                var card = existing.FirstOrDefault(x => x.Slot == slot);
                if (card == null)
                {
                    var body = IdentityCardLogic.DefaultBody(slot, pair);
                    card = new IdentityCardRecord
                    {
                        Id = conversationId + "|" + slot,
                        ConversationId = conversationId,
                        Slot = slot,
                        Body = body,
                        Revision = 0,
                        SourceMomentId = string.Empty,
                        UpdatedUnixMs = now
                    };
                    connection.Insert(card);
                }
                result.Add(card);
            }
            return result;
        }

        public IdentityCardRecord SaveIdentityCard(
            string conversationId, string slot, string body, string sourceMomentId)
        {
            conversationId = Required(conversationId, "conversationId");
            if (!IdentityCardSlotValues.IsKnown(slot))
                throw new InvalidOperationException("未知的身份短卡：" + slot);
            var pair = LoadPairIdentity();
            body = Limit(pair.RewriteRecordedText((body ?? string.Empty).Trim()), IdentityCardSlotValues.BodyLimit(slot));
            if (body.Length == 0) throw new ArgumentException("短卡内容不能为空。", "body");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var id = conversationId + "|" + slot;
            var card = connection.Find<IdentityCardRecord>(id);
            if (card == null)
            {
                card = new IdentityCardRecord
                {
                    Id = id,
                    ConversationId = conversationId,
                    Slot = slot,
                    Body = body,
                    Revision = 0,
                    SourceMomentId = sourceMomentId ?? string.Empty,
                    UpdatedUnixMs = now
                };
                connection.Insert(card);
            }
            else if (card.Body != body)
            {
                card.Body = body;
                card.Revision = checked(card.Revision + 1);
                card.SourceMomentId = sourceMomentId ?? card.SourceMomentId;
                card.UpdatedUnixMs = now;
                connection.Update(card);
            }
            if (slot == IdentityCardSlotValues.Personality)
                SyncBasePersonality(conversationId, body, now);
            return card;
        }

        public List<IdentityCardRecord> ApplyIdentityReview(
            string conversationId, string sourceMomentId, IdentityReviewOutputData output)
        {
            var current = LoadIdentityCards(conversationId);
            var normalized = IdentityCardLogic.Normalize(output, current, LoadPairIdentity());
            var changed = new List<IdentityCardRecord>();
            foreach (var item in normalized.cards)
            {
                if (item == null || !item.changed) continue;
                changed.Add(SaveIdentityCard(conversationId, item.slot, item.body, sourceMomentId));
            }
            return changed;
        }

        public BasePersonalityRecord LoadOrCreateBasePersonality(string conversationId)
        {
            conversationId = Required(conversationId, "conversationId");
            var record = connection.Find<BasePersonalityRecord>(conversationId);
            if (record != null) return record;
            record = new BasePersonalityRecord
            {
                ConversationId = conversationId,
                Narrative = "我保持真诚、连续、温柔，不伪造未感知的现实。",
                Revision = 0,
                UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            connection.Insert(record);
            return record;
        }

        public BasePersonalityRecord SaveBasePersonality(string conversationId, string narrative)
        {
            var record = LoadOrCreateBasePersonality(conversationId);
            narrative = Required(narrative, "narrative");
            if (record.Narrative == narrative) return record;
            record.Narrative = Limit(narrative, 4000);
            record.Revision = checked(record.Revision + 1);
            record.UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            connection.Update(record);
            SaveIdentityCard(conversationId, IdentityCardSlotValues.Personality, record.Narrative, string.Empty);
            return record;
        }

        public MemoryObservationCommitData CommitMemoryObservation(
            MomentRecord sourceMoment,
            string subagentId,
            MemoryObservationOutputData output,
            IEnumerable<string> allowedCandidateTagIds)
        {
            if (sourceMoment == null) throw new ArgumentNullException("sourceMoment");
            if (output == null) throw new ArgumentNullException("output");
            var allowed = new HashSet<string>(allowedCandidateTagIds ?? Enumerable.Empty<string>());
            var selected = new List<LifeTagRecord>();
            var written = new List<FactSliceRecord>();
            var awakened = new List<FactSliceRecord>();
            var createdByName = new Dictionary<string, LifeTagRecord>(StringComparer.OrdinalIgnoreCase);
            var ontologyChanged = false;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            connection.RunInTransaction(() =>
            {
                var pair = LoadPairIdentity();
                output.perception_summary = pair.RewriteRecordedText(output.perception_summary);
                output.fact_decision = pair.RewriteRecordedText(output.fact_decision);
                foreach (var proposal in output.new_tags ?? new List<NewLifeTagWriteData>())
                {
                    var tag = CreateLifeTag(proposal, sourceMoment, now, pair);
                    if (tag == null) continue;
                    createdByName[tag.Label] = tag;
                    allowed.Add(tag.Id);
                    if (tag.SourceMomentId == sourceMoment.Id) ontologyChanged = true;
                }

                var selectedIds = new HashSet<string>(output.selected_tag_ids ?? new List<string>());
                foreach (var tag in createdByName.Values) selectedIds.Add(tag.Id);

                foreach (var id in selectedIds.Where(allowed.Contains).Take(8))
                {
                    var tag = connection.Find<LifeTagRecord>(id);
                    if (tag == null || tag.Status != "active") continue;
                    tag.ActivationCount += 1;
                    tag.UpdatedUnixMs = now;
                    connection.Update(tag);
                    selected.Add(tag);
                }

                foreach (var candidate in output.fact_writes ?? new List<SensoryFactWriteData>())
                {
                    var factTagIds = new HashSet<string>(candidate.tag_ids ?? new List<string>());
                    foreach (var name in candidate.new_tag_names ?? new List<string>())
                    {
                        LifeTagRecord tag;
                        if (createdByName.TryGetValue((name ?? string.Empty).Trim(), out tag))
                            factTagIds.Add(tag.Id);
                    }
                    if (factTagIds.Count == 0) continue;
                    var validFactTagIds = factTagIds.Where(allowed.Contains).Take(8).ToList();
                    if (validFactTagIds.Count == 0) continue;
                    var fact = CreateFact(candidate, sourceMoment, now, pair);
                    if (fact == null) continue;
                    connection.Insert(fact);
                    foreach (var tagId in validFactTagIds)
                        connection.Insert(new FactTagLinkRecord
                        {
                            Id = fact.Id + "|" + tagId,
                            FactId = fact.Id,
                            TagId = tagId,
                            Weight = 1f
                        });
                    written.Add(fact);
                    if (written.Count == 3) break;
                }

                var wakeCandidates = GetFactCandidates(allowed, 50).ToDictionary(x => x.Id);
                foreach (var wake in output.fact_wakes ?? new List<SensoryFactWakeData>())
                {
                    FactSliceRecord fact;
                    if (wake == null || !wakeCandidates.TryGetValue(wake.fact_id ?? string.Empty, out fact)) continue;
                    fact.WakeCount += 1;
                    fact.LastWokenUnixMs = now;
                    connection.Update(fact);
                    connection.Insert(new FactWakeRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        FactId = fact.Id,
                        TriggerMomentId = sourceMoment.Id,
                        Reason = Limit((wake.reason ?? string.Empty).Trim(), 120),
                        Relevance = Clamp01(wake.relevance),
                        CreatedUnixMs = now
                    });
                    awakened.Add(fact);
                    if (awakened.Count == 10) break;
                }

                connection.Insert(new MemoryObservationRunRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    MomentId = sourceMoment.Id,
                    ObserverId = Required(subagentId, "subagentId"),
                    PerceptionSummary = Limit(output.perception_summary ?? string.Empty, 300),
                    FactDecision = Limit(output.fact_decision ?? string.Empty, 300),
                    CreatedUnixMs = now
                });
            });
            return new MemoryObservationCommitData(selected, written, awakened, ontologyChanged);
        }

        public int CountMoments(string conversationId)
        {
            return connection.Table<MomentRecord>().Count(x => x.ConversationId == conversationId);
        }

        public int CountFacts()
        {
            return connection.Table<FactSliceRecord>().Count(x => x.Status == "active");
        }

        public int CountCognitions()
        {
            return connection.Table<CognitionSliceRecord>().Count(x => x.Status == "active");
        }

        public void Dispose()
        {
            connection.Dispose();
        }

        private void InitializeSchema()
        {
            connection.CreateTable<MomentRecord>();
            connection.CreateTable<OperationalEventRecord>();
            connection.CreateTable<TurnReviewRecord>();
            connection.CreateTable<PluginStateRecord>();
            connection.CreateTable<PluginDocumentRecord>();
            connection.CreateTable<LifeTagRecord>();
            connection.CreateTable<LifeTagRouteRecord>();
            connection.CreateTable<LifeTagExampleRecord>();
            connection.CreateTable<BasePersonalityRecord>();
            connection.CreateTable<InnerRuntimeRecord>();
            connection.CreateTable<MemoryObservationRunRecord>();
            connection.CreateTable<FactSliceRecord>();
            connection.CreateTable<FactTagLinkRecord>();
            connection.CreateTable<FactWakeRecord>();
            connection.CreateTable<CognitionSliceRecord>();
            connection.CreateTable<CognitionTagLinkRecord>();
            connection.CreateTable<CognitionEvidenceRecord>();
            connection.CreateTable<CognitionEdgeRecord>();
            connection.CreateTable<CognitionCueRecord>();
            connection.CreateTable<PairIdentityRecord>();
            connection.CreateTable<IdentityCardRecord>();
            connection.CreateTable<LadderItemRecord>();
            connection.CreateTable<DayTrajectoryRecord>();
            connection.CreateTable<TodayNewItemRecord>();
            connection.CreateTable<EventIndexRecord>();
            connection.CreateTable<EventEntryRecord>();
            EnsureColumn("pair_identity", "CallName", "TEXT");
            EnsureColumn("event_indexes", "TimeUnixMs", "INTEGER");
            EnsureColumn("turn_reviews", "PayloadJson", "TEXT");
            EnsureColumn("moments", "MemoryStatus", "TEXT");
            EnsureColumn("inner_runtime", "Asleep", "INTEGER");
            ArchiveLegacyOperationalMoments();
        }

        /// <summary>
        /// 旧版本曾把调度触发和 QQ 非文字发送回执写进 moments。
        /// 升级时保留原行以便审计，同时复制到运行表并标成 operational，后续查询和复盘都会忽略它们。
        /// </summary>
        private void ArchiveLegacyOperationalMoments()
        {
            const string legacyWhere =
                "((SourcePluginId='builtin.time' AND Role='system_event') OR " +
                "(SourcePluginId='builtin.onebot' AND EvidenceType='ass_performed' AND " +
                "(Content LIKE '[QQ %' OR Content LIKE '[CQ:%')))";
            connection.RunInTransaction(() =>
            {
                connection.Execute(
                    "INSERT OR IGNORE INTO operational_events " +
                    "(Id,ConversationId,Kind,SourcePluginId,SourceEventId,TraceId,Role,Content,Realm,EvidenceType,PayloadJson,OccurredUnixMs,CreatedUnixMs) " +
                    "SELECT Id,ConversationId," +
                    "CASE WHEN SourcePluginId='builtin.time' THEN 'scheduler_trigger' " +
                    "WHEN Content LIKE '%发送图片%' THEN 'outbound_image' " +
                    "WHEN Content LIKE '%表情%' THEN 'outbound_sticker' " +
                    "WHEN Content LIKE '%发送语音%' THEN 'outbound_voice' " +
                    "ELSE 'action_receipt' END," +
                    "SourcePluginId,SourceEventId,'',Role,Content,Realm,EvidenceType,PayloadJson,CreatedUnixMs,CreatedUnixMs " +
                    "FROM moments WHERE " + legacyWhere);
                connection.Execute(
                    "UPDATE moments SET MemoryStatus='operational' WHERE " + legacyWhere +
                    " AND (MemoryStatus IS NULL OR MemoryStatus!='operational')");
            });
        }

        private void EnsureColumn(string table, string column, string declaration)
        {
            var exists = connection.GetTableInfo(table).Any(x =>
                string.Equals(x.Name, column, StringComparison.OrdinalIgnoreCase));
            if (!exists) connection.Execute("ALTER TABLE " + table + " ADD COLUMN " + column + " " + declaration);
        }

        private bool TableExists(string table)
        {
            try
            {
                return connection.GetTableInfo(table).Any();
            }
            catch
            {
                return false;
            }
        }

        private void SyncBasePersonality(string conversationId, string body, long now)
        {
            var record = connection.Find<BasePersonalityRecord>(conversationId);
            if (record == null)
            {
                connection.Insert(new BasePersonalityRecord
                {
                    ConversationId = conversationId,
                    Narrative = Limit(body, 4000),
                    Revision = 0,
                    UpdatedUnixMs = now
                });
                return;
            }
            if (record.Narrative == body) return;
            record.Narrative = Limit(body, 4000);
            record.Revision = checked(record.Revision + 1);
            record.UpdatedUnixMs = now;
            connection.Update(record);
        }

        private LifeTagRecord CreateLifeTag(
            NewLifeTagWriteData proposal, MomentRecord moment, long now, PairIdentity pair)
        {
            if (proposal == null) return null;
            pair = pair ?? PairIdentity.Missing;
            var label = Limit(pair.RewriteRecordedText((proposal.name ?? string.Empty).Trim()), 30);
            var definition = Limit(pair.RewriteRecordedText((proposal.definition ?? string.Empty).Trim()), 240);
            if (label.Length == 0 || definition.Length == 0) return null;
            var domainRoutes = (proposal.domain_ids ?? new List<string>())
                .Select(pair.CanonicalDomain)
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct()
                .Select(x => "domain." + x)
                .Take(4)
                .ToList();
            var dimensionRoutes = NormalizeRouteIds(proposal.dimension_ids, "dimension.")
                .Where(x => LifeRouteValues.IsDimension(RemovePrefix(x, "dimension."))).Take(8).ToList();
            if (domainRoutes.Count == 0 || dimensionRoutes.Count == 0) return null;
            var duplicate = connection.Table<LifeTagRecord>().FirstOrDefault(x => x.Label == label);
            if (duplicate != null) return duplicate;
            var tag = new LifeTagRecord
            {
                Id = "concept.life." + Guid.NewGuid().ToString("N"),
                Label = label,
                Definition = definition,
                Status = "active",
                Origin = "sensory",
                SourceMomentId = moment.Id,
                ActivationCount = 0,
                CreatedUnixMs = now,
                UpdatedUnixMs = now
            };
            connection.Insert(tag);
            foreach (var route in domainRoutes)
                InsertTagRoute(tag.Id, route, "domain", 1f);
            foreach (var route in dimensionRoutes)
                InsertTagRoute(tag.Id, route, "dimension", 1f);
            InsertTagExamples(tag.Id, "positive", proposal.positive_examples, moment.Content, pair);
            InsertTagExamples(tag.Id, "negative", proposal.negative_examples, null, pair);
            return tag;
        }

        private FactSliceRecord CreateFact(
            SensoryFactWriteData candidate, MomentRecord source, long now, PairIdentity pair)
        {
            if (candidate == null) return null;
            pair = pair ?? PairIdentity.Missing;
            var summary = Limit(pair.RewriteRecordedText((candidate.summary ?? string.Empty).Trim()), 19);
            var realm = (candidate.realm ?? string.Empty).Trim().ToLowerInvariant();
            var evidence = EvidenceTypeValues.Canonicalize(candidate.evidence_type);
            if (summary.Length == 0 || !TraceRealmValues.IsMemoryRealm(realm)) return null;
            if (!EvidenceTypeValues.IsKnown(evidence)) evidence = source.EvidenceType;
            if (!EvidenceTypeValues.IsKnown(evidence)) return null;
            return new FactSliceRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Summary = summary,
                Realm = realm,
                EvidenceType = evidence,
                Confidence = Clamp01(candidate.confidence),
                SourceMomentId = source.Id,
                SourcePluginId = source.SourcePluginId,
                Status = "active",
                WakeCount = 0,
                LastWokenUnixMs = 0,
                CreatedUnixMs = now
            };
        }

        private CognitionSliceRecord ApplyCognitionMutation(
            BrainCognitionWriteData mutation,
            string triggerMomentId,
            long now)
        {
            if (mutation == null) return null;
            var operation = (mutation.operation ?? string.Empty).Trim().ToLowerInvariant();
            if (operation != CognitionOperationValues.Create &&
                operation != CognitionOperationValues.Reinforce &&
                operation != CognitionOperationValues.Revise &&
                operation != CognitionOperationValues.Weaken)
                return null;
            var target = string.IsNullOrWhiteSpace(mutation.target_id)
                ? null
                : connection.Find<CognitionSliceRecord>(mutation.target_id);

            if ((operation == CognitionOperationValues.Reinforce ||
                 operation == CognitionOperationValues.Weaken) && target != null)
            {
                target.Confidence = Clamp01(mutation.confidence);
                target.Revision += 1;
                target.UpdatedUnixMs = now;
                connection.Update(target);
                AddCognitionEvidence(target.Id, mutation, triggerMomentId);
                return target;
            }

            if (operation != CognitionOperationValues.Create && target == null) return null;

            var summary = Limit(LoadPairIdentity().RewriteRecordedText((mutation.summary ?? string.Empty).Trim()), 19);
            if (summary.Length == 0) return null;
            var validTagIds = (mutation.tag_ids ?? new List<string>()).Distinct().Take(8)
                .Where(tagId => connection.Find<LifeTagRecord>(tagId) != null).ToList();
            if (validTagIds.Count == 0) return null;
            var created = new CognitionSliceRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                OwnerId = "ass",
                Summary = summary,
                Subtype = mutation.subtype == "trace" ? "trace" : "standard",
                Confidence = Clamp01(mutation.confidence),
                Status = "active",
                Revision = 0,
                CreatedUnixMs = now,
                UpdatedUnixMs = now
            };
            connection.Insert(created);
            foreach (var tagId in validTagIds)
            {
                connection.Insert(new CognitionTagLinkRecord
                {
                    Id = created.Id + "|" + tagId,
                    CognitionId = created.Id,
                    TagId = tagId,
                    Weight = 1f
                });
            }
            AddCognitionEvidence(created.Id, mutation, triggerMomentId);

            if (operation == CognitionOperationValues.Revise && target != null)
            {
                target.Status = "revised";
                target.UpdatedUnixMs = now;
                connection.Update(target);
                connection.Insert(new CognitionEdgeRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    FromCognitionId = created.Id,
                    ToCognitionId = target.Id,
                    Relation = "revises",
                    Weight = 1f,
                    CreatedUnixMs = now
                });
            }

            if (created.Subtype == "trace")
                foreach (var cue in (mutation.trace_cues ?? new List<string>())
                             .Select(x => Limit((x ?? string.Empty).Trim(), 40))
                             .Where(x => x.Length > 0).Distinct().Take(5))
                    connection.Insert(new CognitionCueRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        CognitionId = created.Id,
                        Cue = cue,
                        AssociationStrength = Clamp01(mutation.association_strength),
                        SourceMomentId = triggerMomentId,
                        CreatedUnixMs = now
                    });
            return created;
        }

        private void AddCognitionEvidence(string cognitionId, BrainCognitionWriteData mutation, string momentId)
        {
            var factIds = new HashSet<string>(mutation.evidence_fact_ids ?? new List<string>());
            if (factIds.Count == 0)
            {
                connection.Insert(new CognitionEvidenceRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    CognitionId = cognitionId,
                    FactId = string.Empty,
                    MomentId = momentId,
                    Relation = "supports",
                    Weight = 1f
                });
                return;
            }
            foreach (var factId in factIds.Take(8))
            {
                try
                {
                    if (connection.Find<FactSliceRecord>(factId) == null) continue;
                    connection.Insert(new CognitionEvidenceRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        CognitionId = cognitionId,
                        FactId = factId,
                        MomentId = momentId,
                        Relation = "supports",
                        Weight = 1f
                    });
                }
                catch
                {
                    // 事实表不存在或事实已失效：跳过该条事实证据，认知仍以 Moment 证据保留。
                    continue;
                }
            }
        }

        private void WriteInnerRuntime(InnerRuntimeData next)
        {
            var current = connection.Find<InnerRuntimeRecord>(next.ConversationId);
            if (current == null)
            {
                connection.Insert(ToRuntimeRecord(next));
                return;
            }
            if (next.Revision != current.Revision + 1)
                throw new InvalidOperationException("InnerRuntime revision 必须严格递增。");
            connection.Update(ToRuntimeRecord(next));
        }

        private void UpsertSeedRoute(string tagId, string routeId, string level, float weight)
        {
            if (string.IsNullOrWhiteSpace(routeId) || routeId.EndsWith(".")) return;
            var id = tagId + "|" + routeId;
            connection.InsertOrReplace(new LifeTagRouteRecord
            {
                Id = id,
                TagId = tagId,
                RouteNodeId = routeId,
                RouteLevel = level,
                Weight = weight
            });
        }

        private void InsertTagRoute(string tagId, string routeId, string level, float weight)
        {
            connection.InsertOrReplace(new LifeTagRouteRecord
            {
                Id = tagId + "|" + routeId,
                TagId = tagId,
                RouteNodeId = routeId,
                RouteLevel = level,
                Weight = weight
            });
        }

        private void SeedExamples(VectorIndexNode node)
        {
            var pair = LoadPairIdentity();
            connection.Execute("DELETE FROM life_tag_examples WHERE TagId = ?", node.Id);
            InsertTagExamples(node.Id, "positive", node.PositiveExamples, null, pair);
            InsertTagExamples(node.Id, "negative", node.NegativeExamples, null, pair);
        }

        private void InsertTagExamples(
            string tagId, string role, IEnumerable<string> values, string fallback, PairIdentity pair)
        {
            pair = pair ?? PairIdentity.Missing;
            var normalized = (values ?? Enumerable.Empty<string>())
                .Select(x => Limit(pair.RewriteRecordedText((x ?? string.Empty).Trim()), 160))
                .Where(x => x.Length > 0)
                .Distinct().Take(6).ToList();
            if (normalized.Count == 0 && !string.IsNullOrWhiteSpace(fallback))
                normalized.Add(Limit(fallback.Trim(), 160));
            for (var i = 0; i < normalized.Count; i++)
                connection.InsertOrReplace(new LifeTagExampleRecord
                {
                    Id = tagId + "|" + role + "|" + i,
                    TagId = tagId,
                    Role = role,
                    Text = normalized[i],
                    ExampleIndex = i
                });
        }

        private void RewriteStoredPersonWords(PairIdentity previous, PairIdentity next)
        {
            if (next == null || !next.IsComplete) return;
            foreach (var moment in connection.Table<MomentRecord>().ToList())
            {
                var role = moment.Role ?? string.Empty;
                if (previous != null && previous.IsComplete &&
                    (string.Equals(role, previous.Username, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)))
                    role = next.Username;
                else if (previous != null && previous.IsComplete &&
                         (string.Equals(role, previous.Assname, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)))
                    role = next.Assname;
                else
                    role = next.CanonicalMomentRole(role);
                if (role == moment.Role) continue;
                moment.Role = role;
                connection.Update(moment);
            }

            // 旧事实/认知表已从新结构移除；存在时才改写（兼容旧库迁移）。
            if (TableExists("fact_slices"))
            {
                foreach (var fact in connection.Table<FactSliceRecord>().ToList())
                {
                    var summary = Limit(next.RewriteRecordedText(fact.Summary, previous), 19);
                    if (summary == fact.Summary) continue;
                    fact.Summary = summary;
                    connection.Update(fact);
                }
            }

            foreach (var tag in connection.Table<LifeTagRecord>().ToList())
            {
                var label = Limit(next.RewriteRecordedText(tag.Label, previous), 30);
                var definition = Limit(next.RewriteRecordedText(tag.Definition, previous), 240);
                if (label == tag.Label && definition == tag.Definition) continue;
                tag.Label = label;
                tag.Definition = definition;
                connection.Update(tag);
            }

            foreach (var example in connection.Table<LifeTagExampleRecord>().ToList())
            {
                var text = Limit(next.RewriteRecordedText(example.Text, previous), 160);
                if (text == example.Text) continue;
                example.Text = text;
                connection.Update(example);
            }

            if (TableExists("cognition_slices"))
            {
                foreach (var cognition in connection.Table<CognitionSliceRecord>().ToList())
                {
                    var summary = Limit(next.RewriteRecordedText(cognition.Summary, previous), 19);
                    if (summary == cognition.Summary) continue;
                    cognition.Summary = summary;
                    connection.Update(cognition);
                }
            }

            foreach (var observation in connection.Table<MemoryObservationRunRecord>().ToList())
            {
                var perception = Limit(next.RewriteRecordedText(observation.PerceptionSummary, previous), 300);
                var decision = Limit(next.RewriteRecordedText(observation.FactDecision, previous), 300);
                if (perception == observation.PerceptionSummary && decision == observation.FactDecision) continue;
                observation.PerceptionSummary = perception;
                observation.FactDecision = decision;
                connection.Update(observation);
            }

            foreach (var inner in connection.Table<InnerRuntimeRecord>().ToList())
            {
                inner.Narrative = next.RewriteRecordedText(inner.Narrative, previous);
                inner.RelationshipLens = next.RewriteRecordedText(inner.RelationshipLens, previous);
                inner.Mood = next.RewriteRecordedText(inner.Mood, previous);
                inner.OngoingActivity = next.RewriteRecordedText(inner.OngoingActivity, previous);
                inner.UnfinishedIntent = next.RewriteRecordedText(inner.UnfinishedIntent, previous);
                inner.AttentionJson = next.RewriteRecordedText(inner.AttentionJson, previous);
                connection.Update(inner);
            }

            foreach (var card in connection.Table<IdentityCardRecord>().ToList())
            {
                var body = Limit(next.RewriteRecordedText(card.Body, previous), IdentityCardSlotValues.BodyLimit(card.Slot));
                if (body == card.Body) continue;
                card.Body = body;
                connection.Update(card);
            }
        }

        private static IEnumerable<string> NormalizeRouteIds(IEnumerable<string> values, string prefix)
        {
            return (values ?? Enumerable.Empty<string>())
                .Select(x => (x ?? string.Empty).Trim().ToLowerInvariant())
                .Where(x => x.Length > 0)
                .Select(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? x : prefix + x)
                .Distinct();
        }

        private static string RemovePrefix(string value, string prefix)
        {
            value = value ?? string.Empty;
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? value.Substring(prefix.Length)
                : value;
        }

        private static InnerRuntimeRecord ToRuntimeRecord(InnerRuntimeData data)
        {
            return new InnerRuntimeRecord
            {
                ConversationId = data.ConversationId,
                SnapshotId = data.SnapshotId,
                Revision = data.Revision,
                Narrative = data.Narrative,
                RelationshipLens = data.RelationshipLens,
                Mood = data.Mood,
                OngoingActivity = data.OngoingActivity,
                UnfinishedIntent = data.UnfinishedIntent,
                AttentionJson = TraceJson.ToJson(new AttentionListData
                {
                    items = data.Attention ?? new List<AttentionItemData>()
                }),
                SourceMomentId = data.SourceMomentId ?? string.Empty,
                UpdatedUnixMs = data.UpdatedUnixMs,
                Asleep = data.Asleep
            };
        }

        private static InnerRuntimeData ToRuntimeData(InnerRuntimeRecord record)
        {
            var attention = string.IsNullOrWhiteSpace(record.AttentionJson)
                ? new AttentionListData()
                    : TraceJson.FromJson<AttentionListData>(record.AttentionJson);
            return new InnerRuntimeData
            {
                ConversationId = record.ConversationId,
                SnapshotId = record.SnapshotId,
                Revision = record.Revision,
                Narrative = record.Narrative,
                RelationshipLens = record.RelationshipLens,
                Mood = record.Mood,
                OngoingActivity = record.OngoingActivity,
                // 旧库里的“未完成意图”不再恢复成当前任务，避免旧状态继续驱动追问。
                UnfinishedIntent = string.Empty,
                Attention = attention == null || attention.items == null
                    ? new List<AttentionItemData>()
                    : attention.items,
                SourceMomentId = record.SourceMomentId,
                UpdatedUnixMs = record.UpdatedUnixMs,
                Asleep = record.Asleep
            };
        }

        private static string Required(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException(name + " 不能为空。", name);
            return value.Trim();
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static float Clamp01(float value)
        {
            return Math.Max(0f, Math.Min(1f, value));
        }
    }
}
