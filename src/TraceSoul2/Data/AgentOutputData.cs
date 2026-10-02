using System.Collections.Generic;
using System.Linq;

namespace TraceSoul2.Data
{
    /// <summary>模型输出契约。不继承旧心智字段；运行态开口开关、归档、旧工具/媒体字段不进入此类型。</summary>
    public sealed class AgentOutputData
    {
        public string step, reply;
        public bool refine;
        public List<AgentActionData> actions = new();
        public List<AgentGoalUpdateData> goal_updates = new();
        public List<AgentAttentionLinkData> attention_links = new();
        public string affect;
        public string mood, inner, scene, attention, today, new_fact;
        public string location, activity, activity_detail, speak_center, heartbeat_intent, next_heartbeat_plan;
        public bool state_force, sleep;
        public int next_heartbeat_minutes;

        public AgentStepData ToRuntime() => new()
        {
            affect = affect, step = step, reply = reply, refine = refine, goal_updates = goal_updates, attention_links = attention_links,
            actions = actions?.Select(x => x?.ToRuntime()).ToList(),
            mood = mood, inner = inner, scene = scene, attention = attention,
            today = today, new_fact = new_fact, location = location, activity = activity, activity_detail = activity_detail,
            state_force = state_force, speak_center = speak_center, heartbeat_intent = heartbeat_intent,
            next_heartbeat_plan = next_heartbeat_plan, next_heartbeat_minutes = next_heartbeat_minutes, sleep = sleep
        };
    }

    /// <summary>模型只给调用意向；调用标识、身体绑定和执行标识由程序补齐。</summary>
    public sealed class AgentActionData
    {
        public string capability_id, purpose, group_id;
        public List<BrainCallArgumentData> arguments = new();
        public BrainCapabilityCallData ToRuntime() => new()
        { capability_id = capability_id, purpose = purpose, group_id = group_id, arguments = arguments };
    }
}
