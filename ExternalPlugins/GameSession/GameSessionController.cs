using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.ExternalPlugins.GameSession
{
    internal sealed class GameSessionController : IDisposable
    {
        private const string PluginId = "game.session";
        private readonly GameSessionConfig config;
        private readonly GameSessionStore store;
        private readonly TracePluginServices services;
        private readonly StardewGameAdapter stardewAdapter;
        private readonly SemaphoreSlim mutation = new SemaphoreSlim(1, 1);
        private readonly ConcurrentQueue<PluginEventData> outgoing = new ConcurrentQueue<PluginEventData>();
        private readonly object workGate = new object();
        private readonly HashSet<string> tickInFlight = new HashSet<string>(StringComparer.Ordinal);
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public GameSessionController(GameSessionConfig config, GameSessionStore store,
            TracePluginServices services, StardewGameAdapter stardewAdapter = null)
        {
            this.config = config ?? throw new ArgumentNullException("config");
            this.store = store ?? throw new ArgumentNullException("store");
            this.services = services ?? throw new ArgumentNullException("services");
            this.stardewAdapter = stardewAdapter;
        }

        public CancellationToken ShutdownToken { get { return shutdown.Token; } }

        public async Task<Tuple<GameSessionRecord, PluginEventData>> StartAsync(
            string conversationId, string profileId, string gameId, string title,
            string adapterId, string roleInstruction, string environmentJson,
            bool queueNotification, CancellationToken token)
        {
            conversationId = Required(conversationId, "conversation_id");
            await mutation.WaitAsync(token);
            try
            {
                if (store.GetActive(conversationId) != null)
                    throw new InvalidOperationException("这个对话已经有一场进行中的游戏，请先结束它。");
                var profile = ResolveProfile(profileId);
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var session = new GameSessionRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ConversationId = conversationId,
                    ProfileId = profile.id,
                    AdapterId = string.IsNullOrWhiteSpace(adapterId) ? profile.adapter_id : adapterId.Trim(),
                    GameId = string.IsNullOrWhiteSpace(gameId) ? profile.id : gameId.Trim(),
                    Title = string.IsNullOrWhiteSpace(title) ? profile.title : title.Trim(),
                    Status = GameSessionStatusValues.Active,
                    StartedUnixMs = now,
                    LastEventUnixMs = now,
                    LastSummaryUnixMs = now,
                    LastSyncUnixMs = now,
                    CurrentSummary = string.Empty,
                    CurrentObjective = string.Empty,
                    CurrentStateJson = "{}",
                    OpenThreadsJson = "[]",
                    IdentityBase = BuildIdentityBase(conversationId, profile,
                        string.IsNullOrWhiteSpace(roleInstruction) ? profile.role_instruction : roleInstruction.Trim()),
                    RoleInstruction = string.IsNullOrWhiteSpace(roleInstruction)
                        ? profile.role_instruction : roleInstruction.Trim(),
                    EnvironmentJson = NormalizeJson(environmentJson, "{}"),
                    SyncMode = profile.sync_mode,
                    SyncIntervalMinutes = profile.sync_interval_minutes,
                    TimeoutMinutes = profile.session_timeout_minutes
                };
                store.InsertSession(session);
                StardewStartupResult stardew = null;
                if (stardewAdapter != null && stardewAdapter.CanHandle(session))
                {
                    try
                    {
                        stardew = await stardewAdapter.StartSessionAsync(session, token);
                        store.AppendEvent(session.Id, "bridge_connected", "system",
                            stardew.Content, JsonSerializer.Serialize(new
                            {
                                companion = stardew.Companion,
                                control_mode = stardew.ControlMode,
                                confirmed = true
                            }), stardew.StateJson, now);
                        session = store.Get(session.Id);
                    }
                    catch (Exception exception)
                    {
                        try { await stardewAdapter.StopSessionAsync(session.Id, false, CancellationToken.None); }
                        catch { }
                        session.Status = GameSessionStatusValues.Aborted;
                        session.EndedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        store.UpdateSession(session);
                        if (exception is OperationCanceledException) throw;
                        throw new InvalidOperationException("星露谷没有真正进入：" + exception.Message, exception);
                    }
                }
                UpdateLifeState(session, true);
                var produced = RuntimeEvent(session, "game_session_started",
                    (stardew == null
                        ? "我们开始一起玩《" + session.Title + "》了。"
                        : "游戏侧已经确认：" + stardew.Content) +
                    (string.IsNullOrWhiteSpace(session.RoleInstruction)
                        ? string.Empty : "这场里我的位置是：" + session.RoleInstruction), now);
                if (queueNotification) outgoing.Enqueue(produced);
                return Tuple.Create(session, produced);
            }
            finally { mutation.Release(); }
        }

        public async Task<GameEventRecord> AppendEventAsync(GameEventInput input, CancellationToken token)
        {
            if (input == null) throw new ArgumentNullException("input");
            var sessionId = Required(input.session_id, "session_id");
            var content = Required(input.content, "content");
            await mutation.WaitAsync(token);
            try
            {
                var record = store.AppendEvent(sessionId,
                    NormalizeKind(input.kind), NormalizeActor(input.actor), Limit(content, 12000),
                    SerializeLoose(input.payload, "{}"), SerializeLoose(input.state, "{}"),
                    input.occurred_unix_ms);
                await SummarizeCoreAsync(sessionId, false, token);
                return record;
            }
            finally { mutation.Release(); }
        }

        public GameSessionRecord Status(string conversationId, string sessionId = null)
        {
            if (!string.IsNullOrWhiteSpace(sessionId)) return store.Get(sessionId.Trim());
            return store.GetActive((conversationId ?? string.Empty).Trim());
        }

        public async Task<GameSessionEndResult> EndAsync(string sessionId, string conversationId,
            bool abort, bool queueEvent, CancellationToken token)
        {
            await mutation.WaitAsync(token);
            try
            {
                var session = !string.IsNullOrWhiteSpace(sessionId)
                    ? store.Get(sessionId.Trim()) : store.GetActive(Required(conversationId, "conversation_id"));
                if (session == null) throw new InvalidOperationException("没有找到进行中的游戏会话。");
                if (session.Status != GameSessionStatusValues.Active)
                    return new GameSessionEndResult { Session = session };
                if (stardewAdapter != null && stardewAdapter.CanHandle(session))
                    await stardewAdapter.StopSessionAsync(session.Id, true, token);
                return await FinalizeCoreAsync(session, abort, queueEvent, token);
            }
            finally { mutation.Release(); }
        }

        public string BuildFacet(string conversationId, int maxChars)
        {
            var session = store.GetActive((conversationId ?? string.Empty).Trim());
            if (session == null) return null;
            var builder = new StringBuilder();
            builder.Append("【当前游戏】和她一起玩：").AppendLine(session.Title);
            if (!string.IsNullOrWhiteSpace(session.CurrentSummary))
                builder.Append("阶段进度：").AppendLine(session.CurrentSummary.Trim());
            else
            {
                var recent = store.GetRecentEvents(session.Id, 8);
                if (recent.Count > 0)
                    builder.Append("刚刚发生：").AppendLine(string.Join("；", recent.Select(CompactEvent)));
                else builder.AppendLine("阶段进度：刚刚开始，还没有游戏事件。");
            }
            if (!string.IsNullOrWhiteSpace(session.CurrentObjective))
                builder.Append("当前目标：").AppendLine(session.CurrentObjective.Trim());
            var threads = DeserializeStrings(session.OpenThreadsJson);
            if (threads.Count > 0)
                builder.Append("仍未收束：").AppendLine(string.Join("；", threads));
            builder.AppendLine("这是临时游戏工作台，不是长期记忆；回答她时只在问题相关时使用。");
            if (stardewAdapter != null && stardewAdapter.PublicStatus(session.Id) != null)
                builder.Append("Stardew MCP 已确认游戏中生成了以 Soul 名字显示的角色。Follow 动作来自 SMAPI Mod 的跟随状态机；只有 Player 模式的动作来自绑定 identity_base 的本地 Agent。游戏事实只以桥接回报为准。");
            else
                builder.Append("游戏会话已经建立；是否真正进入游戏，只以适配器的确认事件为准。");
            return Limit(builder.ToString().Trim(), Math.Max(300, maxChars));
        }

        public object PublicSession(GameSessionRecord session, bool includeIdentityBase)
        {
            if (session == null) return null;
            if (session.Status == GameSessionStatusValues.Active && stardewAdapter != null &&
                stardewAdapter.CanHandle(session))
                stardewAdapter.EnsureRunning(session);
            return new
            {
                session_id = session.Id,
                conversation_id = session.ConversationId,
                profile_id = session.ProfileId,
                adapter_id = session.AdapterId,
                game_id = session.GameId,
                title = session.Title,
                status = session.Status,
                started_unix_ms = session.StartedUnixMs,
                ended_unix_ms = session.EndedUnixMs,
                event_count = session.EventCount,
                summarized_through_seq = session.SummarizedThroughSeq,
                summary = session.CurrentSummary ?? string.Empty,
                objective = session.CurrentObjective ?? string.Empty,
                state = ParseLoose(session.CurrentStateJson),
                open_threads = DeserializeStrings(session.OpenThreadsJson),
                role_instruction = session.RoleInstruction ?? string.Empty,
                identity_base = includeIdentityBase ? session.IdentityBase ?? string.Empty : null,
                sync_mode = session.SyncMode,
                sync_interval_minutes = session.SyncIntervalMinutes,
                timeout_minutes = session.TimeoutMinutes,
                adapter_runtime = stardewAdapter?.PublicStatus(session.Id)
            };
        }

        public object PublicHistory(string conversationId, string sessionId, int take)
        {
            var session = Status(conversationId, sessionId);
            if (session == null) return null;
            take = Math.Max(1, Math.Min(100, take <= 0 ? 40 : take));
            var events = store.GetRecentEvents(session.Id, take);
            var checkpoints = store.GetCheckpoints(session.Id);
            return new
            {
                session = PublicSession(session, false),
                events = events.Select(x => new
                {
                    seq = x.Seq,
                    kind = x.Kind ?? string.Empty,
                    actor = x.Actor ?? string.Empty,
                    content = x.Content ?? string.Empty,
                    occurred_unix_ms = x.CreatedUnixMs,
                    summarized = x.Summarized
                }).ToList(),
                checkpoints = checkpoints.TakeLast(8).Select(x => new
                {
                    from_seq = x.FromSeq,
                    to_seq = x.ToSeq,
                    summary = x.Summary ?? string.Empty,
                    objective = x.Objective ?? string.Empty,
                    occurred_unix_ms = x.CreatedUnixMs
                }).ToList()
            };
        }

        public void Tick(long nowUnixMs)
        {
            foreach (var session in store.GetActiveSessions())
            {
                lock (workGate)
                {
                    if (!tickInFlight.Add(session.Id)) continue;
                }
                _ = Task.Run(async () =>
                {
                    try { await TickSessionAsync(session.Id, nowUnixMs, shutdown.Token); }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                    catch (Exception exception)
                    {
                        services.LogTiming(null, "游戏会话后台检查失败", detail: exception.Message);
                    }
                    finally { lock (workGate) tickInFlight.Remove(session.Id); }
                });
            }
        }

        public List<PluginEventData> DrainOutgoing()
        {
            var result = new List<PluginEventData>();
            PluginEventData item;
            while (outgoing.TryDequeue(out item)) result.Add(item);
            return result;
        }

        private async Task TickSessionAsync(string sessionId, long nowUnixMs, CancellationToken token)
        {
            await mutation.WaitAsync(token);
            try
            {
                var session = store.Get(sessionId);
                if (session == null || session.Status != GameSessionStatusValues.Active) return;
                if (stardewAdapter != null && stardewAdapter.CanHandle(session))
                {
                    stardewAdapter.EnsureRunning(session);
                    foreach (var item in stardewAdapter.DrainEvents(session.Id))
                        store.AppendEvent(session.Id, NormalizeKind(item.Kind), NormalizeActor(item.Actor),
                            Limit(item.Content, 12000), item.PayloadJson, item.StateJson,
                            item.OccurredUnixMs);
                    session = store.Get(session.Id);
                }
                var idleMs = Math.Max(0, nowUnixMs - session.LastEventUnixMs);
                if (idleMs >= session.TimeoutMinutes * 60000L)
                {
                    await FinalizeCoreAsync(session, false, true, token);
                    return;
                }
                if (session.EventCount > session.SummarizedThroughSeq &&
                    idleMs >= config.summary_idle_minutes * 60000L)
                    await SummarizeCoreAsync(session.Id, true, token);

                session = store.Get(session.Id);
                if (session == null || session.Status != GameSessionStatusValues.Active ||
                    session.SyncMode != "timed" || string.IsNullOrWhiteSpace(session.CurrentSummary)) return;
                if (nowUnixMs - session.LastSyncUnixMs < session.SyncIntervalMinutes * 60000L) return;
                session.LastSyncUnixMs = nowUnixMs;
                store.UpdateSession(session);
                outgoing.Enqueue(RuntimeEvent(session, "game_session_progress",
                    "游戏《" + session.Title + "》进行到这里：" + session.CurrentSummary +
                    (string.IsNullOrWhiteSpace(session.CurrentObjective)
                        ? string.Empty : " 当前目标：" + session.CurrentObjective), nowUnixMs));
            }
            finally { mutation.Release(); }
        }

        private async Task<GameSummaryData> SummarizeCoreAsync(string sessionId, bool force,
            CancellationToken token)
        {
            var session = store.Get(sessionId);
            if (session == null || session.Status != GameSessionStatusValues.Active) return null;
            var events = store.GetUnsummarized(session.Id);
            if (events.Count == 0) return null;
            var chars = events.Sum(x => (x.Content ?? string.Empty).Length);
            if (!force && events.Count < config.summary_event_count && chars < config.summary_char_count)
                return null;

            GameSummaryData summary;
            var client = services.ReviewLlm ?? services.Llm;
            try
            {
                if (client == null) throw new InvalidOperationException("没有可用的摘要模型。");
                var response = await client.CompleteJsonAsync(new List<DeepSeekMessageData>
                {
                    new DeepSeekMessageData("system",
                        "你是游戏事件压缩器。只写中性事实，不推测人物感受，不补未发生的情节。" +
                        "把上一版摘要与新增事件合并为当前完整阶段状态。只输出 JSON：" +
                        "{\"summary\":\"\",\"objective\":\"\",\"state\":{},\"open_threads\":[]}"),
                    new DeepSeekMessageData("user", BuildSummaryInput(session, events))
                }, token);
                summary = ParseSummary(response);
                if (string.IsNullOrWhiteSpace(summary.summary))
                    throw new InvalidOperationException("摘要模型没有返回 summary。");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                services.LogTiming(null, "游戏阶段摘要回退", detail: exception.Message);
                summary = FallbackSummary(session, events);
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var fromSeq = events.First().Seq;
            var toSeq = events.Last().Seq;
            session = store.Get(session.Id);
            session.CurrentSummary = Limit(summary.summary.Trim(), 1200);
            session.CurrentObjective = Limit((summary.objective ?? string.Empty).Trim(), 300);
            session.CurrentStateJson = SerializeLoose(summary.state, session.CurrentStateJson ?? "{}");
            session.OpenThreadsJson = JsonSerializer.Serialize(
                (summary.open_threads ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => Limit(x.Trim(), 200)).Take(10).ToList());
            session.SummarizedThroughSeq = Math.Max(session.SummarizedThroughSeq, toSeq);
            session.LastSummaryUnixMs = now;
            store.CommitCheckpoint(session, new GameCheckpointRecord
            {
                SessionId = session.Id,
                FromSeq = fromSeq,
                ToSeq = toSeq,
                Summary = session.CurrentSummary,
                Objective = session.CurrentObjective,
                StateJson = session.CurrentStateJson,
                OpenThreadsJson = session.OpenThreadsJson,
                CreatedUnixMs = now
            });
            return summary;
        }

        private async Task<GameSessionEndResult> FinalizeCoreAsync(GameSessionRecord session,
            bool abort, bool queueEvent, CancellationToken token)
        {
            if (!abort) await SummarizeCoreAsync(session.Id, true, token);
            session = store.Get(session.Id);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            session.Status = abort ? GameSessionStatusValues.Aborted : GameSessionStatusValues.Finished;
            session.EndedUnixMs = now;
            PluginEventData produced;
            if (abort)
            {
                produced = RuntimeEvent(session, "game_session_aborted",
                    "《" + session.Title + "》这次游戏会话已中止，这一把不计入共同经历。", now);
            }
            else
            {
                var finalText = await BuildFinalSummaryAsync(session, now, token);
                produced = new PluginEventData
                {
                    PluginId = PluginId,
                    ConversationId = session.ConversationId,
                    ExternalEventId = "game-session-final:" + session.Id,
                    Role = "system_event",
                    Content = finalText,
                    Realm = TraceRealmValues.SharedScene,
                    EvidenceType = EvidenceTypeValues.PluginObserved,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        session_id = session.Id,
                        profile_id = session.ProfileId,
                        adapter_id = session.AdapterId,
                        game_id = session.GameId,
                        from_seq = session.EventCount == 0 ? 0 : 1,
                        to_seq = session.EventCount,
                        started_unix_ms = session.StartedUnixMs,
                        ended_unix_ms = now,
                        transition = "left_game",
                        previous_activity = "游戏",
                        current_activity = string.Empty,
                        exit_reason = "finished"
                    }),
                    IsOperational = false,
                    Wake = KernelWakeValues.Mind,
                    OccurredUnixMs = now
                };
                session.FinalEventQueued = queueEvent;
            }
            store.UpdateSession(session);
            UpdateLifeState(session, false);
            if (queueEvent) outgoing.Enqueue(produced);
            return new GameSessionEndResult { Session = session, Event = produced };
        }

        private async Task<string> BuildFinalSummaryAsync(GameSessionRecord session, long endedUnixMs,
            CancellationToken token)
        {
            var pair = services.Storage.LoadPairIdentity();
            var who = pair != null && pair.IsComplete ? "我和" + pair.Username : "我们";
            var started = DateTimeOffset.FromUnixTimeMilliseconds(session.StartedUnixMs).ToLocalTime();
            var elapsedMs = Math.Max(0L, endedUnixMs - session.StartedUnixMs);
            var durationMinutes = Math.Max(1L, elapsedMs / 60000L);
            var frame = durationMinutes < 2
                ? started.ToString("M月d日tt") + "，" + who + "短暂进入《" + session.Title + "》玩了一会儿。"
                : started.ToString("M月d日tt") + "，" + who + "一起玩了约" +
                  FriendlyDuration(durationMinutes) + "《" + session.Title + "》。";
            var experienceEvents = store.GetRecentEvents(session.Id, 60)
                .Where(IsFinalExperienceEvent)
                .ToList();
            var eventEvidence = string.Join("；", experienceEvents.Select(CompactEvent));
            var summary = experienceEvents.Count == 0
                ? string.Empty : CleanFinalExperienceSummary(session.CurrentSummary);
            if (string.IsNullOrWhiteSpace(summary))
                summary = CleanFinalExperienceSummary(eventEvidence);
            var fallback = string.IsNullOrWhiteSpace(summary)
                ? "这次还没来得及留下具体的游戏进度。"
                : summary;
            var client = services.ReviewLlm ?? services.Llm;
            if (client != null && experienceEvents.Count > 0)
            {
                try
                {
                    var response = await client.CompleteTextAsync(new List<DeepSeekMessageData>
                    {
                        new DeepSeekMessageData("system",
                            "把下面已经结束的一次共同游戏经历压成一段不超过180字的历史记录。" +
                            "只保留游戏里实际发生的地点变化、行动、选择和结果，不写感受，不补写未确认的事。" +
                            "这是离开游戏后的记录，不是当前状态或任务清单；禁止写当前目标、未完事项、下一步、继续或等待。" +
                            "禁止出现 Companion、MCP、SMAPI、Mod、Agent、LLM、桥接、连接、模式等内部实现词。" +
                            "只输出一段纯文本，不要重复离开游戏的开场句，也不要输出 HTML 实体。"),
                        new DeepSeekMessageData("user", "游戏：" + session.Title +
                            "\n已确认的游戏经历：" + fallback)
                    }, token, null);
                    var cleaned = CleanFinalExperienceSummary(response);
                    if (!string.IsNullOrWhiteSpace(cleaned)) summary = cleaned;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    services.LogTiming(null, "游戏最终摘要回退", detail: exception.Message);
                }
            }
            if (string.IsNullOrWhiteSpace(summary)) summary = fallback;
            var exitFrame = "我们已经结束了这次《" + session.Title +
                            "》的游玩，从游戏里出来，回到了平常的相处场景。";
            return exitFrame + frame + Limit(summary, 220);
        }

        private string BuildIdentityBase(string conversationId, GameProfileConfig profile,
            string roleInstruction)
        {
            var pair = services.Storage.LoadPairIdentity();
            var cards = services.Storage.LoadIdentityCards(conversationId)
                .Where(x => x != null && profile.identity_slots.Contains(x.Slot)).ToList();
            var builder = new StringBuilder();
            if (pair != null && pair.IsComplete) builder.Append("我是").Append(pair.Assname).AppendLine("。");
            foreach (var slot in profile.identity_slots)
            {
                var card = cards.FirstOrDefault(x => x.Slot == slot);
                if (card == null || string.IsNullOrWhiteSpace(card.Body)) continue;
                builder.Append("【").Append(IdentityCardSlotValues.Title(slot, pair)).Append("】")
                    .AppendLine(card.Body.Trim());
            }
            if (!string.IsNullOrWhiteSpace(roleInstruction))
                builder.Append("【这场游戏里的位置】").AppendLine(roleInstruction.Trim());
            builder.Append("【事实边界】游戏事实只以工具和游戏环境的实际回报为准，不伪造未感知或未发生的事。");
            return Limit(builder.ToString().Trim(), profile.identity_budget_chars);
        }

        private void UpdateLifeState(GameSessionRecord session, bool active)
        {
            if (services.LifeState == null || session == null) return;
            services.LifeState.Update(session.ConversationId, new LifeStatePatchData
            {
                activity = active ? "游戏" : string.Empty,
                activity_detail = active ? session.Title : string.Empty,
                source = LifeStateSourceValues.Plugin,
                source_id = session.Id
            });
        }

        private static PluginEventData RuntimeEvent(GameSessionRecord session, string kind,
            string content, long occurredUnixMs)
        {
            return new PluginEventData
            {
                PluginId = PluginId,
                ConversationId = session.ConversationId,
                ExternalEventId = kind + ":" + session.Id + ":" + occurredUnixMs,
                Role = "system_event",
                Content = content,
                Realm = TraceRealmValues.SharedScene,
                EvidenceType = EvidenceTypeValues.PluginObserved,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    kind,
                    session_id = session.Id,
                    game_id = session.GameId,
                    title = session.Title,
                    event_count = session.EventCount
                }),
                IsOperational = true,
                Wake = KernelWakeValues.Mind,
                OccurredUnixMs = occurredUnixMs
            };
        }

        private GameProfileConfig ResolveProfile(string id)
        {
            id = string.IsNullOrWhiteSpace(id) ? "generic" : id.Trim();
            var profile = config.profiles.FirstOrDefault(x =>
                string.Equals(x.id, id, StringComparison.OrdinalIgnoreCase));
            if (profile == null && config.profiles.Count == 1) profile = config.profiles[0];
            if (profile == null) throw new InvalidOperationException("没有游戏档案：" + id);
            return profile;
        }

        private static string BuildSummaryInput(GameSessionRecord session, List<GameEventRecord> events)
        {
            var builder = new StringBuilder();
            builder.AppendLine("游戏：" + session.Title);
            builder.AppendLine("上一版摘要：" + (session.CurrentSummary ?? string.Empty));
            builder.AppendLine("上一版目标：" + (session.CurrentObjective ?? string.Empty));
            builder.AppendLine("新增事件：");
            foreach (var item in events)
                builder.Append(item.Seq).Append('|').Append(item.Kind).Append('|').Append(item.Actor)
                    .Append('|').AppendLine(Limit(item.Content, 1000));
            return builder.ToString();
        }

        private static GameSummaryData ParseSummary(string value)
        {
            value = StripCodeFence(value);
            return JsonSerializer.Deserialize<GameSummaryData>(value, JsonOptions) ?? new GameSummaryData();
        }

        private static GameSummaryData FallbackSummary(GameSessionRecord session, List<GameEventRecord> events)
        {
            var recent = events.TakeLast(18).Select(CompactEvent).ToList();
            var prefix = string.IsNullOrWhiteSpace(session.CurrentSummary)
                ? string.Empty : session.CurrentSummary.Trim() + "；";
            return new GameSummaryData
            {
                summary = Limit(prefix + string.Join("；", recent), 1200),
                objective = session.CurrentObjective ?? string.Empty,
                state = ParseLoose(events.LastOrDefault(x => !string.IsNullOrWhiteSpace(x.StateJson))?.StateJson
                    ?? session.CurrentStateJson),
                open_threads = DeserializeStrings(session.OpenThreadsJson)
            };
        }

        private static string CompactEvent(GameEventRecord item)
        {
            if (item == null) return string.Empty;
            var actor = string.IsNullOrWhiteSpace(item.Actor) ? string.Empty : item.Actor + "：";
            return actor + Limit((item.Content ?? string.Empty).Trim(), 180);
        }

        private static bool IsFinalExperienceEvent(GameEventRecord item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Content)) return false;
            var kind = (item.Kind ?? string.Empty).Trim().ToLowerInvariant();
            if (kind.Contains("error", StringComparison.Ordinal) ||
                kind.Contains("offline", StringComparison.Ordinal) ||
                kind.Contains("reconnect", StringComparison.Ordinal) ||
                kind.Contains("connected", StringComparison.Ordinal) ||
                kind.Contains("bridge", StringComparison.Ordinal) ||
                kind.Contains("adapter", StringComparison.Ordinal) ||
                kind.Contains("model", StringComparison.Ordinal)) return false;
            return !string.Equals(kind, "agent_ready", StringComparison.Ordinal) &&
                   !string.Equals(kind, "companion_ready", StringComparison.Ordinal);
        }

        private static string CleanFinalExperienceSummary(string value)
        {
            value = WebUtility.HtmlDecode(StripCodeFence(value ?? string.Empty));
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var banned = new[]
            {
                "companion", "mcp", "smapi", "mod", "agent", "llm", "bridge",
                "桥接", "连接", "重连", "离线", "状态机", "人格模型", "身份提示", "跟随模式",
                "player 模式", "follow 模式", "现处", "当前目标", "未完事项", "下一步",
                "继续跟随", "继续等待", "等待指令", "下次"
            };
            var clauses = value.Split(new[] { '。', '！', '？', '；', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(x => string.Join(" ", x.Split((char[])null,
                    StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '，', ',', ';'))
                .Where(x => x.Length > 0 && !banned.Any(marker =>
                    x.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
            return clauses.Count == 0 ? string.Empty : Limit(string.Join("。", clauses) + "。", 220);
        }

        private static string NormalizeKind(string value)
        {
            var text = (value ?? string.Empty).Trim().ToLowerInvariant();
            return text.Length == 0 ? "system" : Limit(text, 40);
        }

        private static string NormalizeActor(string value)
        {
            var text = (value ?? string.Empty).Trim().ToLowerInvariant();
            return text == "user" || text == "companion" || text == "world" ? text : "world";
        }

        private static string Required(string value, string name)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0) throw new ArgumentException(name + " 不能为空。", name);
            return value;
        }

        private static string NormalizeJson(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            try { using (JsonDocument.Parse(value)) return value.Trim(); }
            catch { return fallback; }
        }

        private static string SerializeLoose(object value, string fallback)
        {
            if (value == null) return fallback;
            if (value is JsonElement) return ((JsonElement)value).GetRawText();
            if (value is string) return NormalizeJson((string)value, fallback);
            return JsonSerializer.Serialize(value);
        }

        private static object ParseLoose(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new { };
            try { return JsonDocument.Parse(value).RootElement.Clone(); }
            catch { return value; }
        }

        private static List<string> DeserializeStrings(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new List<string>();
            try { return JsonSerializer.Deserialize<List<string>>(value) ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        private static string StripCodeFence(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (!value.StartsWith("```", StringComparison.Ordinal)) return value;
            var firstNewline = value.IndexOf('\n');
            var lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
            return firstNewline >= 0 && lastFence > firstNewline
                ? value.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim() : value;
        }

        private static string FriendlyDuration(long minutes)
        {
            if (minutes < 60) return minutes + "分钟";
            var hours = minutes / 60;
            var rest = minutes % 60;
            return rest == 0 ? hours + "小时" : hours + "小时" + rest + "分钟";
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, Math.Max(0, max));
        }

        public void Dispose()
        {
            shutdown.Cancel();
            try { stardewAdapter?.Dispose(); } catch { }
            var entered = false;
            try { entered = mutation.Wait(TimeSpan.FromSeconds(10)); }
            catch (ObjectDisposedException) { }
            if (entered)
            {
                try { store.Dispose(); }
                finally
                {
                    mutation.Release();
                    mutation.Dispose();
                }
            }
            else
            {
                // 极慢的第三方 LLM 若没有及时响应取消，不在热重扫线程里硬等；
                // 让旧实例完成后自行释放私库，期间 ALC 会被这项任务安全地托住。
                _ = Task.Run(() =>
                {
                    try
                    {
                        mutation.Wait();
                        store.Dispose();
                        mutation.Release();
                        mutation.Dispose();
                    }
                    catch { }
                });
            }
            shutdown.Dispose();
        }
    }
}
