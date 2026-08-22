using System;
using System.Collections.Generic;
using SQLite;

namespace TraceSoul2.Data
{
    [Serializable]
    public sealed class AttentionItemData
    {
        public string kind;
        public string content;
        public List<string> source_refs = new List<string>();
    }

    [Serializable]
    public sealed class AttentionListData
    {
        public List<AttentionItemData> items = new List<AttentionItemData>();
    }

    /// <summary>唯一 Brain 的持久内心：描述“此刻的我”，不复制长期记忆。</summary>
    public sealed class InnerRuntimeData
    {
        public string ConversationId { get; set; }
        public string SnapshotId { get; set; }
        public int Revision { get; set; }
        public string Narrative { get; set; }
        public string RelationshipLens { get; set; }
        public string Mood { get; set; }
        public string OngoingActivity { get; set; }
        public string UnfinishedIntent { get; set; }
        public List<AttentionItemData> Attention { get; set; } = new List<AttentionItemData>();
        public string SourceMomentId { get; set; }
        public long UpdatedUnixMs { get; set; }
        /// <summary>睡着后心跳停，非打破性 Moment 不跑心智。</summary>
        public bool Asleep { get; set; }
    }

    [Table("inner_runtime")]
    public sealed class InnerRuntimeRecord
    {
        [PrimaryKey]
        public string ConversationId { get; set; }
        public string SnapshotId { get; set; }
        public int Revision { get; set; }
        public string Narrative { get; set; }
        public string RelationshipLens { get; set; }
        public string Mood { get; set; }
        public string OngoingActivity { get; set; }
        public string UnfinishedIntent { get; set; }
        public string AttentionJson { get; set; }
        public string SourceMomentId { get; set; }
        public long UpdatedUnixMs { get; set; }
        public bool Asleep { get; set; }
    }

    [Serializable]
    public sealed class AttentionWriteData
    {
        public string kind;
        public string content;
    }

    [Serializable]
    public sealed class InnerRuntimeWriteData
    {
        public string narrative;
        public string relationship_update;
        public string mood;
        public string ongoing_activity;
        public string unfinished_intent;
        /// <summary>null=不改手上；空列表=放下。</summary>
        public List<AttentionWriteData> attention;
        /// <summary>null=不改睡眠；true=睡下；false=醒来。</summary>
        public bool? asleep;
    }
}
