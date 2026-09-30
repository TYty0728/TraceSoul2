using System;
using System.Collections.Generic;
using System.Linq;
using TraceSoul2.Data;

namespace TraceSoul2.Logic
{
    /// <summary>一轮 Agent 的状态增量。控制流与行动不继承；状态省略保留，显式值修订。</summary>
    public sealed class AgentTurnStateLogic
    {
        private static readonly System.Reflection.FieldInfo[] Fields = typeof(AgentOutputData).GetFields()
            .Where(x => x.Name is not ("step" or "reply" or "refine" or "actions" or "goal_updates")).ToArray();
        private readonly Dictionary<string, object> values = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, object> Values => values;

        public void Update(AgentOutputData output, IReadOnlyCollection<string> providedFields)
        {
            if (providedFields == null) throw new ArgumentNullException(nameof(providedFields));
            foreach (var field in Fields)
                if (providedFields.Contains(field.Name, StringComparer.OrdinalIgnoreCase))
                    values[field.Name] = field.GetValue(output);
        }

        public void ApplyTo(AgentOutputData output)
        {
            foreach (var field in Fields)
                if (values.TryGetValue(field.Name, out var value)) field.SetValue(output, value);
        }
    }
}
