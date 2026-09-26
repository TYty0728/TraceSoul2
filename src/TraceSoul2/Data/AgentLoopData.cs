using System.Collections.Generic;

namespace TraceSoul2.Data
{
    /// <summary>一次 Agent 推进。旧状态字段保留 ABI；正文与行动不再经过必选的第二个模型。</summary>
    public sealed class AgentStepData : MindDecisionData
    {
        public string step = "finish";
        public string reply;
        public bool refine;
        public List<BrainCapabilityCallData> actions = new List<BrainCapabilityCallData>();
    }

    /// <summary>设备回执。确认的内容与进度来自执行端，不能从模型计划推算。</summary>
    public sealed class TraceExecutionReceiptData
    {
        public string ExecutionId { get; set; }
        public long Sequence { get; set; }
        public string Status { get; set; }
        public long? ProgressMs { get; set; }
        public string ConfirmedContent { get; set; }
        public string Summary { get; set; }
    }

    public sealed class TraceExecutionSnapshotData
    {
        public string ExecutionId { get; set; }
        public string ConversationId { get; set; }
        public string CapabilityId { get; set; }
        public string BodyId { get; set; }
        public string GroupId { get; set; }
        public string Status { get; set; }
        public long Sequence { get; set; }
        public long? ProgressMs { get; set; }
        public string ConfirmedContent { get; set; }
        public string Summary { get; set; }
    }
}
