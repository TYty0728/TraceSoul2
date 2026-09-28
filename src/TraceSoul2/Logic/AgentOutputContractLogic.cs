using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TraceSoul2.Data;

namespace TraceSoul2.Logic
{
    /// <summary>提示中的类型结构与实际模型 DTO 使用同一个来源，防止提示/解析契约漂移。</summary>
    public static class AgentOutputContractLogic
    {
        public static readonly string SchemaJson = JsonSerializer.Serialize(Shape(typeof(AgentOutputData)));
        public static readonly string Prompt = BuildPrompt();

        private static string BuildPrompt()
        {
            var builder = new StringBuilder("【唯一输出结构】\n以下是字段类型定义，不是 JSON 示例；输出须为合法 JSON，只使用下列字段。根对象必须给 step，其余按需省略，不填 null。boolean 只能 true/false，integer 必须整数。插件参数只放 actions.arguments，value 始终为 string，复杂值编码为 JSON 字符串。\n");
            var pending = new Queue<Type>();
            var seen = new HashSet<Type>();
            pending.Enqueue(typeof(AgentOutputData));
            string ObjectName(Type type) => type.Name.Replace("Data", "").Replace("Agent", "").Replace("BrainCall", "");
            string Name(Type type)
            {
                if (type == typeof(string)) return "string";
                if (type == typeof(bool)) return "boolean";
                if (type == typeof(int)) return "integer";
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                    return Name(type.GetGenericArguments()[0]) + "[]";
                pending.Enqueue(type);
                return ObjectName(type);
            }
            while (pending.Count > 0)
            {
                var type = pending.Dequeue();
                if (!seen.Add(type)) continue;
                builder.AppendLine(type == typeof(AgentOutputData) ? "根对象：" : ObjectName(type) + "：");
                foreach (var group in type.GetFields(BindingFlags.Instance | BindingFlags.Public).GroupBy(x => x.FieldType))
                    builder.Append("  ").Append(string.Join(", ", group.Select(x => x.Name)))
                        .Append(": ").AppendLine(Name(group.Key));
            }
            return builder.ToString().TrimEnd();
        }

        private static object Shape(Type type)
        {
            if (type == typeof(string)) return new { type = "string" };
            if (type == typeof(bool)) return new { type = "boolean" };
            if (type == typeof(int)) return new { type = "integer" };
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return new { type = "array", items = Shape(type.GetGenericArguments()[0]) };
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            return new { type = "object", properties = fields.ToDictionary(x => x.Name, x => Shape(x.FieldType)),
                required = type == typeof(AgentOutputData) ? new[] { "step" } : Array.Empty<string>(), additionalProperties = false };
        }
    }
}
