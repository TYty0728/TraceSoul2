using System;
using System.Collections.Generic;
using System.IO;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Util;

namespace TraceSoul2.Host
{
    /// <summary>
    /// 当前生活状态的宿主实现。位置/活动分别做来源优先级仲裁，插件不需要直接改 bodies.json。
    /// </summary>
    public sealed class JsonLifeStateStore : ILifeStateStore
    {
        private readonly string path;
        private readonly string dataDirectory;
        private readonly object gate = new object();

        public JsonLifeStateStore(string dataDirectory)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
                throw new ArgumentException("dataDirectory 不能为空。", "dataDirectory");
            Directory.CreateDirectory(dataDirectory);
            this.dataDirectory = dataDirectory;
            path = Path.Combine(dataDirectory, "life-state.json");
        }

        public LifeStateData Load(string conversationId)
        {
            var id = NormalizeConversationId(conversationId);
            lock (gate)
            {
                var document = Read();
                LifeStateData value;
                if (!document.states.TryGetValue(id, out value) || value == null)
                {
                    value = NewState(id, MouthLogic.LoadState(dataDirectory).scene);
                    document.states[id] = value;
                    Write(document);
                }
                return Clone(value);
            }
        }

        public LifeStateData Update(string conversationId, LifeStatePatchData patch)
        {
            var id = NormalizeConversationId(conversationId);
            patch = patch ?? new LifeStatePatchData();
            var source = NormalizeSource(patch.source);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (gate)
            {
                var document = Read();
                LifeStateData state;
                if (!document.states.TryGetValue(id, out state) || state == null)
                    state = NewState(id, MouthLogic.LoadState(dataDirectory).scene);

                if (patch.location != null &&
                    CanReplace(state.location_source, source, patch.force))
                {
                    state.location = BodySceneValues.Normalize(patch.location);
                    state.location_source = source;
                    state.location_source_id = patch.source_id ?? string.Empty;
                    state.location_updated_unix_ms = now;
                }

                if (patch.activity != null &&
                    CanReplace(state.activity_source, source, patch.force))
                {
                    var activity = NormalizeActivity(patch.activity);
                    var changed = !string.Equals(state.activity, activity, StringComparison.Ordinal);
                    state.activity = activity;
                    state.activity_detail = activity.Length == 0
                        ? string.Empty
                        : Limit(patch.activity_detail, 160);
                    state.activity_source = source;
                    state.activity_source_id = patch.source_id ?? string.Empty;
                    state.activity_updated_unix_ms = now;
                    if (changed) state.activity_started_unix_ms = activity.Length == 0 ? 0 : now;
                }

                document.states[id] = state;
                Write(document);
                // 旧身体路由仍以 bodies.json 的 scene 为入口；保持它与新状态契约同步。
                MouthLogic.SetScene(Path.GetDirectoryName(path), state.location);
                return Clone(state);
            }
        }

        private sealed class Document
        {
            public Dictionary<string, LifeStateData> states =
                new Dictionary<string, LifeStateData>(StringComparer.OrdinalIgnoreCase);
        }

        private Document Read()
        {
            if (!File.Exists(path)) return new Document();
            try
            {
                var value = TraceJson.FromJson<Document>(File.ReadAllText(path));
                if (value == null) return new Document();
                if (value.states == null)
                    value.states = new Dictionary<string, LifeStateData>(StringComparer.OrdinalIgnoreCase);
                return value;
            }
            catch { return new Document(); }
        }

        private void Write(Document document)
        {
            File.WriteAllText(path, TraceJson.ToJson(document ?? new Document()));
        }

        private static LifeStateData NewState(string conversationId, string initialLocation)
        {
            return new LifeStateData
            {
                conversation_id = conversationId,
                location = BodySceneValues.Normalize(initialLocation),
                activity = string.Empty,
                location_source = LifeStateSourceValues.System,
                activity_source = LifeStateSourceValues.System
            };
        }

        private static LifeStateData Clone(LifeStateData source)
        {
            if (source == null) return null;
            return new LifeStateData
            {
                conversation_id = source.conversation_id,
                location = source.location,
                activity = source.activity,
                activity_detail = source.activity_detail,
                location_source = source.location_source,
                activity_source = source.activity_source,
                location_source_id = source.location_source_id,
                activity_source_id = source.activity_source_id,
                location_updated_unix_ms = source.location_updated_unix_ms,
                activity_updated_unix_ms = source.activity_updated_unix_ms,
                activity_started_unix_ms = source.activity_started_unix_ms
            };
        }

        private static bool CanReplace(string existing, string incoming, bool force)
        {
            return force || LifeStateSourceValues.Priority(incoming) >=
                LifeStateSourceValues.Priority(existing);
        }

        private static string NormalizeSource(string source)
        {
            var value = (source ?? string.Empty).Trim().ToLowerInvariant();
            if (value == LifeStateSourceValues.User || value == LifeStateSourceValues.Sensor ||
                value == LifeStateSourceValues.Plugin || value == LifeStateSourceValues.Mind)
                return value;
            return LifeStateSourceValues.System;
        }

        private static string NormalizeActivity(string value)
        {
            var text = (value ?? string.Empty).Trim();
            if (text == "无" || text == "空闲" || text == "没有") return string.Empty;
            return Limit(text, 80);
        }

        private static string NormalizeConversationId(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "tracesoul2" : value.Trim();
        }

        private static string Limit(string value, int max)
        {
            value = value ?? string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
