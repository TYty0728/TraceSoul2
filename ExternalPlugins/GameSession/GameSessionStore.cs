using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SQLite;

namespace TraceSoul2.ExternalPlugins.GameSession
{
    /// <summary>游戏工作台私库。所有访问在插件自己的锁内完成，不与 Soul 主库混用连接。</summary>
    internal sealed class GameSessionStore : IDisposable
    {
        private readonly object gate = new object();
        private readonly SQLiteConnection connection;

        public string DatabasePath { get; private set; }

        public GameSessionStore(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("游戏会话数据库路径不能为空。", "databasePath");
            DatabasePath = Path.GetFullPath(databasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");
            ResetReloadedTypeMappings();
            connection = new SQLiteConnection(DatabasePath);
            connection.EnableWriteAheadLogging();
            connection.CreateTable<GameSessionRecord>();
            connection.CreateTable<GameEventRecord>();
            connection.CreateTable<GameCheckpointRecord>();
            connection.Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_game_events_session_seq ON game_events(SessionId, Seq)");
        }

        /// <summary>
        /// sqlite-net 1.9 caches mappings in a process-wide dictionary keyed only by Type.FullName.
        /// A collectible external plugin gets new Type instances after a hot rescan, so stale PropertyInfo
        /// entries otherwise throw TargetException when the first row is inserted or read.
        /// </summary>
        private static void ResetReloadedTypeMappings()
        {
            var field = typeof(SQLiteConnection).GetField("_mappings",
                BindingFlags.Static | BindingFlags.NonPublic);
            var mappings = field == null ? null : field.GetValue(null) as IDictionary;
            if (mappings == null) return;
            lock (mappings)
            {
                mappings.Remove(typeof(GameSessionRecord).FullName);
                mappings.Remove(typeof(GameEventRecord).FullName);
                mappings.Remove(typeof(GameCheckpointRecord).FullName);
            }
        }

        public GameSessionRecord Get(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return null;
            lock (gate) return connection.Find<GameSessionRecord>(sessionId.Trim());
        }

        public GameSessionRecord GetActive(string conversationId)
        {
            conversationId = (conversationId ?? string.Empty).Trim();
            lock (gate)
                return connection.Table<GameSessionRecord>()
                    .Where(x => x.ConversationId == conversationId && x.Status == GameSessionStatusValues.Active)
                    .OrderByDescending(x => x.StartedUnixMs)
                    .FirstOrDefault();
        }

        public List<GameSessionRecord> GetActiveSessions()
        {
            lock (gate)
                return connection.Table<GameSessionRecord>()
                    .Where(x => x.Status == GameSessionStatusValues.Active)
                    .OrderBy(x => x.StartedUnixMs)
                    .ToList();
        }

        public void InsertSession(GameSessionRecord session)
        {
            if (session == null) throw new ArgumentNullException("session");
            lock (gate) connection.Insert(session);
        }

        public void UpdateSession(GameSessionRecord session)
        {
            if (session == null) throw new ArgumentNullException("session");
            lock (gate) connection.Update(session);
        }

        public GameEventRecord AppendEvent(string sessionId, string kind, string actor, string content,
            string payloadJson, string stateJson, long occurredUnixMs)
        {
            lock (gate)
            {
                var session = connection.Find<GameSessionRecord>(sessionId);
                if (session == null || session.Status != GameSessionStatusValues.Active)
                    throw new InvalidOperationException("游戏会话不存在或已经结束：" + sessionId);
                var nextSeq = session.EventCount + 1;
                var record = new GameEventRecord
                {
                    SessionId = session.Id,
                    Seq = nextSeq,
                    Kind = kind ?? string.Empty,
                    Actor = actor ?? string.Empty,
                    Content = content ?? string.Empty,
                    PayloadJson = payloadJson ?? string.Empty,
                    StateJson = stateJson ?? string.Empty,
                    CreatedUnixMs = occurredUnixMs > 0
                        ? occurredUnixMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Summarized = false
                };
                connection.RunInTransaction(() =>
                {
                    connection.Insert(record);
                    session.EventCount = nextSeq;
                    session.CharCount += record.Content.Length;
                    session.LastEventUnixMs = record.CreatedUnixMs;
                    if (!string.IsNullOrWhiteSpace(record.StateJson))
                        session.CurrentStateJson = record.StateJson;
                    connection.Update(session);
                });
                return record;
            }
        }

        public List<GameEventRecord> GetUnsummarized(string sessionId, int take = 1000)
        {
            lock (gate)
                return connection.Table<GameEventRecord>()
                    .Where(x => x.SessionId == sessionId && !x.Summarized)
                    .OrderBy(x => x.Seq)
                    .Take(Math.Max(1, Math.Min(5000, take)))
                    .ToList();
        }

        public List<GameEventRecord> GetRecentEvents(string sessionId, int take)
        {
            lock (gate)
                return connection.Table<GameEventRecord>()
                    .Where(x => x.SessionId == sessionId)
                    .OrderByDescending(x => x.Seq)
                    .Take(Math.Max(1, Math.Min(200, take)))
                    .ToList()
                    .OrderBy(x => x.Seq)
                    .ToList();
        }

        public List<GameCheckpointRecord> GetCheckpoints(string sessionId)
        {
            lock (gate)
                return connection.Table<GameCheckpointRecord>()
                    .Where(x => x.SessionId == sessionId)
                    .OrderBy(x => x.ToSeq)
                    .ToList();
        }

        public void CommitCheckpoint(GameSessionRecord session, GameCheckpointRecord checkpoint)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (checkpoint == null) throw new ArgumentNullException("checkpoint");
            lock (gate)
            {
                connection.RunInTransaction(() =>
                {
                    connection.Insert(checkpoint);
                    connection.Execute(
                        "UPDATE game_events SET Summarized = 1 WHERE SessionId = ? AND Seq >= ? AND Seq <= ?",
                        session.Id, checkpoint.FromSeq, checkpoint.ToSeq);
                    connection.Update(session);
                });
            }
        }

        public void Dispose()
        {
            lock (gate) connection.Dispose();
        }
    }
}
