using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TraceSoul2.Data;

namespace TraceSoul2.Logic
{
    /// <summary>提示中的类型结构与实际模型 DTO 使用同一个来源，防止提示/解析契约漂移。</summary>
    public static class AgentOutputContractLogic
    {
        public static readonly string Prompt = "【唯一输出结构】\n以下 schema 由实际解析类型生成。只填写这里的字段；可选字段没有变化就省略。字符串、布尔、整数、数组和对象不能互换，非 nullable 字段不要填 null。插件格式只属于 actions 的参数，不能扩展根 JSON。arguments 的 value 始终是字符串，复杂参数先序列化成字符串。\n" +
            JsonSerializer.Serialize(Shape(typeof(AgentOutputData)));

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
