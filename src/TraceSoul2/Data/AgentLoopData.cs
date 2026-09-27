using System.Collections.Generic;

namespace TraceSoul2.Data
{
    /// <summary>一次 Agent 推进。旧状态字段保留 ABI；正文与行动不再经过必选的第二个模型。</summary>
    public sealed class AgentStepData : MindDecisionData
    {
        // 缺少步骤是无效输出，不能默认解释为一次有意的安静决定。
        public string step;
        public string reply;
        public bool refine;
        public List<AgentGoalUpdateData> goal_updates = new List<AgentGoalUpdateData>();
        public List<AgentAttentionLinkData> attention_links = new List<AgentAttentionLinkData>();
        public List<BrainCapabilityCallData> actions = new List<BrainCapabilityCallData>();
    }

    /// <summary>当下/未来目标及明确偏好；同一次 Agent 生成内提出，由内核校验后保存。</summary>
    public sealed class AgentGoalUpdateData
    {
        public string operation; // create / revise / complete / cancel
        public string id; // 修改时必须使用当前目标目录的 ID
        public string kind; // preference / goal
        public string content;
        public string applies_when;
        public string horizon; // turn / ongoing / until
        public string starts_at; // 可选 ISO 8601，必须带时区
        public string expires_at; // until 必填
        public string source; // user / self / feedback
        public string evidence_quote;
    }

    public sealed class AgentAttentionLinkData
    {
        public string attention;
        public List<string> cognition_ids = new List<string>();
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
