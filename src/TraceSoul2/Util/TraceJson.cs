using System.Text.Encodings.Web;
using System.Text.Json;

namespace TraceSoul2.Util
{
    /// <summary>TraceSoul2 的统一 JSON 入口；支持现有数据契约中的公开字段。</summary>
    public static class TraceJson
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string ToJson(object value)
        {
            return JsonSerializer.Serialize(value, Options);
        }

        public static T FromJson<T>(string json)
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
    }
}
