using System;
using System.Collections.Generic;
using SQLite;

namespace TraceSoul2.ExternalPlugins.GameSession
{
    internal static class GameSessionStatusValues
    {
        public const string Active = "active";
        public const string Finished = "finished";
        public const string Aborted = "aborted";
    }

    internal sealed class GameProfileConfig
    {
        public string id { get; set; } = "generic";
        public string title { get; set; } = "通用游戏";
        public string adapter_id { get; set; } = "generic";
        public List<string> identity_slots { get; set; } = new List<string> { "personality", "self" };
        public int identity_budget_chars { get; set; } = 1600;
        public string role_instruction { get; set; } = "你是他的游戏观察者与副驾驶；只依据游戏工具回报理解局面。";
        public string sync_mode { get; set; } = string.Empty;
        public int sync_interval_minutes { get; set; }
        public int session_timeout_minutes { get; set; }
    }

    internal sealed class GameSessionConfig
    {
        public string websocket_path { get; set; } = "/plugins/game-session/ws";
        public string access_token { get; set; } = string.Empty;
        public int summary_event_count { get; set; } = 30;
        public int summary_char_count { get; set; } = 8000;
        public int summary_idle_minutes { get; set; } = 20;
        public string sync_mode { get; set; } = "timed";
        public int sync_interval_minutes { get; set; } = 60;
        public int session_timeout_minutes { get; set; } = 120;
        public int facet_max_chars { get; set; } = 1200;
        public string profiles_json { get; set; } = string.Empty;
        public List<GameProfileConfig> profiles { get; set; } = new List<GameProfileConfig>();
    }

    [Table("game_sessions")]
    internal sealed class GameSessionRecord
    {
        [PrimaryKey] public string Id { get; set; }
        [Indexed] public string ConversationId { get; set; }
        public string ProfileId { get; set; }
        public string AdapterId { get; set; }
        public string GameId { get; set; }
        public string Title { get; set; }
        [Indexed] public string Status { get; set; }
        public long StartedUnixMs { get; set; }
        public long EndedUnixMs { get; set; }
        public int EventCount { get; set; }
        public int CharCount { get; set; }
        public int SummarizedThroughSeq { get; set; }
        public string CurrentSummary { get; set; }
        public string CurrentObjective { get; set; }
        public string CurrentStateJson { get; set; }
        public string OpenThreadsJson { get; set; }
        public string IdentityBase { get; set; }
        public string RoleInstruction { get; set; }
        public string EnvironmentJson { get; set; }
        public string SyncMode { get; set; }
        public int SyncIntervalMinutes { get; set; }
        public int TimeoutMinutes { get; set; }
        public long LastEventUnixMs { get; set; }
        public long LastSummaryUnixMs { get; set; }
        public long LastSyncUnixMs { get; set; }
        public bool FinalEventQueued { get; set; }
    }

    [Table("game_events")]
    internal sealed class GameEventRecord
    {
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        [Indexed] public string SessionId { get; set; }
        public int Seq { get; set; }
        public string Kind { get; set; }
        public string Actor { get; set; }
        public string Content { get; set; }
        public string PayloadJson { get; set; }
        public string StateJson { get; set; }
        public long CreatedUnixMs { get; set; }
        [Indexed] public bool Summarized { get; set; }
    }

    [Table("game_checkpoints")]
    internal sealed class GameCheckpointRecord
    {
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        [Indexed] public string SessionId { get; set; }
        public int FromSeq { get; set; }
        public int ToSeq { get; set; }
        public string Summary { get; set; }
        public string Objective { get; set; }
        public string StateJson { get; set; }
        public string OpenThreadsJson { get; set; }
        public long CreatedUnixMs { get; set; }
    }

    internal sealed class GameEventInput
    {
        public string session_id { get; set; }
        public string kind { get; set; }
        public string actor { get; set; }
        public string content { get; set; }
        public object payload { get; set; }
        public object state { get; set; }
        public long occurred_unix_ms { get; set; }
    }

    internal sealed class GameSummaryData
    {
        public string summary { get; set; } = string.Empty;
        public string objective { get; set; } = string.Empty;
        public object state { get; set; }
        public List<string> open_threads { get; set; } = new List<string>();
    }

    internal sealed class GameSessionEndResult
    {
        public GameSessionRecord Session { get; set; }
        public TraceSoul2.Data.PluginEventData Event { get; set; }
    }
}
