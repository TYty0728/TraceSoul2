using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TraceSoul2.Plugins.Builtin;

namespace TraceSoul2.Host
{
    /// <summary>
    /// 器官包配置：plugin.json 是值，config_schema.json 是表单（AstrBot 同款：标签 + 说明 + 控件）。
    /// dll 字段由加载器使用，控制台不能改。
    /// </summary>
    public static class PluginConfigStore
    {
        private static JsonSerializerOptions PrettyOptions(bool includeFields = false)
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true,
                IncludeFields = includeFields,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
            };
        }

        public sealed class Field
        {
            public string key { get; set; }
            public string label { get; set; }
            public string description { get; set; }
            public string type { get; set; }
            public string placeholder { get; set; }
            public string role { get; set; }
            public string provider_key { get; set; }
            public JsonElement? @default { get; set; }
            public List<string> options { get; set; }
            public double? min { get; set; }
            public double? max { get; set; }
            public double? step { get; set; }
        }

        public sealed class SaveResult
        {
            public bool saved { get; set; }
            public bool restart { get; set; }
            public string message { get; set; }
        }

        public sealed class Schema
        {
            public string hint { get; set; }
            public List<Field> fields { get; set; }
        }

        public static object ReadPackage(string packageDirectory, string dllName)
        {
            var schema = LoadSchema(packageDirectory) ?? InferSchema(packageDirectory);
            var values = LoadPluginJson(packageDirectory);
            return BuildForm(schema, values, dllName, restartRequired: false, folder: packageDirectory);
        }

        public static void WritePackage(
            string packageDirectory,
            string dllName,
            Dictionary<string, JsonElement> incoming)
        {
            if (string.IsNullOrWhiteSpace(packageDirectory))
                throw new InvalidOperationException("没有插件包目录。");
            Directory.CreateDirectory(packageDirectory);
            var path = Path.Combine(packageDirectory, "plugin.json");
            var schema = LoadSchema(packageDirectory) ?? InferSchema(packageDirectory);
            var node = LoadPluginObject(path);
            JsonValue dllValue = node["dll"] as JsonValue;
            if (!string.IsNullOrWhiteSpace(dllName) &&
                (dllValue == null || string.IsNullOrWhiteSpace(dllValue.ToString())))
                node["dll"] = dllName;
            var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (incoming != null)
            {
                foreach (var pair in incoming)
                    if (!string.IsNullOrWhiteSpace(pair.Key)) map[pair.Key] = pair.Value;
            }
            foreach (var field in schema.fields ?? new List<Field>())
            {
                if (field == null || string.IsNullOrWhiteSpace(field.key)) continue;
                if (string.Equals(field.key, "dll", StringComparison.OrdinalIgnoreCase)) continue;
                JsonElement raw;
                if (!map.TryGetValue(field.key, out raw)) continue;
                node[field.key] = Coerce(raw, field);
            }
            File.WriteAllText(path, node.ToJsonString(PrettyOptions()));
        }

        public static object ReadOneBot(string dataDirectory)
        {
            var config = OneBotConfig.Load(dataDirectory);
            var values = new JsonObject
            {
                ["enabled"] = config.enabled,
                ["mode"] = config.mode ?? "reverse",
                ["listen_port"] = config.listen_port <= 0 ? 9021 : config.listen_port,
                ["ws_url"] = config.ws_url ?? "",
                ["http_url"] = config.http_url ?? "",
                ["access_token"] = config.access_token ?? "",
                ["self_id"] = config.self_id ?? "",
                ["reply_enabled"] = config.reply_enabled
            };
            return BuildForm(OneBotSchema(), values, null, restartRequired: true, folder: dataDirectory);
        }

        public static void WriteOneBot(string dataDirectory, Dictionary<string, JsonElement> incoming)
        {
            var current = OneBotConfig.Load(dataDirectory);
            incoming = incoming ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            var config = new OneBotConfig
            {
                enabled = ReadBool(incoming, "enabled", current.enabled),
                mode = ReadString(incoming, "mode", current.mode ?? "reverse") == "forward" ? "forward" : "reverse",
                listen_port = (int)Math.Max(1, Math.Min(65535, ReadNumber(incoming, "listen_port",
                    current.listen_port <= 0 ? 9021 : current.listen_port))),
                ws_url = ReadString(incoming, "ws_url", current.ws_url),
                http_url = ReadString(incoming, "http_url", current.http_url),
                access_token = ReadString(incoming, "access_token", current.access_token),
                self_id = ReadString(incoming, "self_id", current.self_id),
                reply_enabled = ReadBool(incoming, "reply_enabled", current.reply_enabled)
            };
            File.WriteAllText(
                Path.Combine(dataDirectory, "onebot.json"),
                JsonSerializer.Serialize(config, PrettyOptions(includeFields: true)));
        }

        private static object BuildForm(
            Schema schema, JsonObject values, string dllName, bool restartRequired, string folder)
        {
            var fields = new List<object>();
            foreach (var field in schema.fields ?? new List<Field>())
            {
                if (field == null || string.IsNullOrWhiteSpace(field.key)) continue;
                if (string.Equals(field.key, "dll", StringComparison.OrdinalIgnoreCase)) continue;
                fields.Add(new
                {
                    field.key,
                    label = string.IsNullOrWhiteSpace(field.label) ? field.key : field.label,
                    description = field.description ?? string.Empty,
                    type = NormalizeType(field.type, field.key),
                    placeholder = field.placeholder ?? string.Empty,
                    role = field.role ?? string.Empty,
                    provider_key = field.provider_key ?? string.Empty,
                    options = field.options ?? new List<string>(),
                    min = field.min,
                    max = field.max,
                    step = field.step,
                    value = ResolveValue(values, field)
                });
            }
            return new
            {
                hint = schema.hint ?? string.Empty,
                dll = dllName ?? string.Empty,
                folder = folder ?? string.Empty,
                restartRequired,
                fields
            };
        }

        private static Schema OneBotSchema()
        {
            return new Schema
            {
                hint = "NapCat 反向连 ws://127.0.0.1:{端口}/ws，token 与此处一致。保存后宿主会重启（约 2 秒）。",
                fields = new List<Field>
                {
                    FieldOf("enabled", "启用 QQ 平台", "关掉则不监听 NapCat，器官也发不出去。", "bool"),
                    new Field
                    {
                        key = "mode", label = "连接模式",
                        description = "反向：NapCat 连我们（推荐，AstrBot aiocqhttp 同款）。正向：我们连 NapCat。",
                        type = "select",
                        options = new List<string> { "reverse", "forward" }
                    },
                    new Field
                    {
                        key = "listen_port", label = "反向监听端口",
                        description = "NapCat websocketClients 填 ws://127.0.0.1:此端口/ws。",
                        type = "number", min = 1, max = 65535, step = 1
                    },
                    FieldOf("access_token", "Access Token", "可填多个，逗号/分号分隔；与 NapCat 的 token 对应。空=不校验。", "password"),
                    FieldOf("self_id", "只收这个 QQ", "self_id。留空 = 都收。", "string"),
                    FieldOf("reply_enabled", "回发到 QQ", "开：文字回复自动发回 QQ。关：只收不回，回复留在控制台。", "bool"),
                    FieldOf("ws_url", "正向 WS 地址", "仅正向模式使用。", "string"),
                    FieldOf("http_url", "正向 HTTP 动作地址", "仅正向模式使用；反向模式动作走同一根 WS。", "string")
                }
            };
        }

        private static Field FieldOf(string key, string label, string description, string type)
        {
            return new Field { key = key, label = label, description = description, type = type };
        }

        private static Schema LoadSchema(string packageDirectory)
        {
            var path = Path.Combine(packageDirectory ?? string.Empty, "config_schema.json");
            if (!File.Exists(path)) return null;
            try
            {
                return JsonSerializer.Deserialize<Schema>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Schema();
            }
            catch
            {
                return null;
            }
        }

        private static Schema InferSchema(string packageDirectory)
        {
            var schema = new Schema
            {
                hint = "这个包没有 config_schema.json，按 plugin.json 里现有字段生成表单。",
                fields = new List<Field>()
            };
            var values = LoadPluginJson(packageDirectory);
            foreach (var pair in values)
            {
                if (string.Equals(pair.Key, "dll", StringComparison.OrdinalIgnoreCase)) continue;
                schema.fields.Add(new Field
                {
                    key = pair.Key,
                    label = pair.Key,
                    type = GuessType(pair.Key, pair.Value)
                });
            }
            return schema;
        }

        private static JsonObject LoadPluginJson(string packageDirectory)
        {
            return LoadPluginObject(Path.Combine(packageDirectory ?? string.Empty, "plugin.json"));
        }

        private static JsonObject LoadPluginObject(string path)
        {
            if (!File.Exists(path)) return new JsonObject();
            try
            {
                return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            catch
            {
                return new JsonObject();
            }
        }

        private static object ResolveValue(JsonObject values, Field field)
        {
            JsonNode node;
            if (values != null && values.TryGetPropertyValue(field.key, out node) && node != null)
                return NodeToForm(node, field);
            if (field.@default.HasValue && field.@default.Value.ValueKind != JsonValueKind.Undefined &&
                field.@default.Value.ValueKind != JsonValueKind.Null)
                return JsonElementToForm(field.@default.Value, field);
            if (NormalizeType(field.type, field.key) == "bool") return false;
            if (NormalizeType(field.type, field.key) == "list") return "";
            return "";
        }

        private static object NodeToForm(JsonNode node, Field field)
        {
            var type = NormalizeType(field.type, field.key);
            if (type == "bool") return node.GetValue<bool>();
            if (type == "number")
            {
                if (node is JsonValue jv && jv.TryGetValue<double>(out var d)) return d;
                return 0;
            }
            if (type == "list")
            {
                if (node is JsonArray array)
                    return string.Join(", ", array.Select(x => x == null ? "" : x.ToString()).Where(x => x.Length > 0));
                return node.ToString();
            }
            return node.ToString();
        }

        private static object JsonElementToForm(JsonElement el, Field field)
        {
            var type = NormalizeType(field.type, field.key);
            if (type == "bool") return el.ValueKind == JsonValueKind.True;
            if (type == "number") return el.ValueKind == JsonValueKind.Number ? el.GetDouble() : 0;
            if (type == "list" && el.ValueKind == JsonValueKind.Array)
                return string.Join(", ", el.EnumerateArray().Select(x => x.ToString()).Where(x => x.Length > 0));
            return el.ToString();
        }

        private static JsonNode Coerce(JsonElement raw, Field field)
        {
            var type = NormalizeType(field.type, field.key);
            if (type == "bool")
            {
                if (raw.ValueKind == JsonValueKind.True || raw.ValueKind == JsonValueKind.False)
                    return raw.GetBoolean();
                var text = raw.ToString().Trim();
                return text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            if (type == "number")
            {
                if (raw.ValueKind == JsonValueKind.Number) return raw.GetDouble();
                double n;
                return double.TryParse(raw.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out n) ? n : 0;
            }
            if (type == "list")
            {
                var array = new JsonArray();
                if (raw.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in raw.EnumerateArray())
                    {
                        var text = item.ToString().Trim();
                        if (text.Length > 0) array.Add(text);
                    }
                    return array;
                }
                foreach (var part in raw.ToString().Split(new[] { ',', '，', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var text = part.Trim();
                    if (text.Length > 0) array.Add(text);
                }
                return array;
            }
            return raw.ValueKind == JsonValueKind.Null ? "" : raw.ToString();
        }

        private static string NormalizeType(string type, string key)
        {
            type = (type ?? string.Empty).Trim().ToLowerInvariant();
            if (type == "password" || type == "number" || type == "bool" || type == "boolean" ||
                type == "select" || type == "list" || type == "text" || type == "textarea" ||
                type == "provider" || type == "provider_model")
                return type == "boolean" ? "bool" : (type == "textarea" ? "text" : type);
            key = (key ?? string.Empty).ToLowerInvariant();
            if (key.IndexOf("api_key", StringComparison.Ordinal) >= 0 ||
                key.IndexOf("token", StringComparison.Ordinal) >= 0 ||
                key.IndexOf("secret", StringComparison.Ordinal) >= 0 ||
                key.IndexOf("password", StringComparison.Ordinal) >= 0)
                return "password";
            return "string";
        }

        private static string GuessType(string key, JsonNode node)
        {
            if (node is JsonValue jv)
            {
                bool b;
                double d;
                if (jv.TryGetValue(out b)) return "bool";
                if (jv.TryGetValue(out d)) return "number";
            }
            if (node is JsonArray) return "list";
            return NormalizeType(null, key);
        }

        private static bool ReadBool(Dictionary<string, JsonElement> incoming, string key, bool fallback)
        {
            JsonElement el;
            if (!incoming.TryGetValue(key, out el)) return fallback;
            if (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False) return el.GetBoolean();
            var text = el.ToString().Trim();
            if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1") return true;
            if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0") return false;
            return fallback;
        }

        private static string ReadString(Dictionary<string, JsonElement> incoming, string key, string fallback)
        {
            JsonElement el;
            if (!incoming.TryGetValue(key, out el) || el.ValueKind == JsonValueKind.Null) return fallback ?? string.Empty;
            return el.ToString();
        }

        private static double ReadNumber(Dictionary<string, JsonElement> incoming, string key, double fallback)
        {
            JsonElement el;
            if (!incoming.TryGetValue(key, out el)) return fallback;
            if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
            double n;
            return double.TryParse(el.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out n) ? n : fallback;
        }
    }
}
