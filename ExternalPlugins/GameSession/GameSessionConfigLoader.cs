using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TraceSoul2.Data;

namespace TraceSoul2.ExternalPlugins.GameSession
{
    internal static class GameSessionConfigLoader
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public static GameSessionConfig Load(string packageDirectory, string dataDirectory)
        {
            var result = new GameSessionConfig();
            Apply(result, Path.Combine(packageDirectory ?? string.Empty, "plugin.json"));
            Apply(result, Path.Combine(dataDirectory ?? string.Empty, "config.json"));
            Normalize(result);
            return result;
        }

        private static void Apply(GameSessionConfig target, string path)
        {
            if (!File.Exists(path)) return;
            using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var root = doc.RootElement;
                target.websocket_path = String(root, "websocket_path") ?? target.websocket_path;
                target.access_token = String(root, "access_token") ?? target.access_token;
                target.summary_event_count = Int(root, "summary_event_count", target.summary_event_count);
                target.summary_char_count = Int(root, "summary_char_count", target.summary_char_count);
                target.summary_idle_minutes = Int(root, "summary_idle_minutes", target.summary_idle_minutes);
                target.sync_mode = String(root, "sync_mode") ?? target.sync_mode;
                target.sync_interval_minutes = Int(root, "sync_interval_minutes", target.sync_interval_minutes);
                target.session_timeout_minutes = Int(root, "session_timeout_minutes", target.session_timeout_minutes);
                target.facet_max_chars = Int(root, "facet_max_chars", target.facet_max_chars);
                target.profiles_json = String(root, "profiles_json") ?? target.profiles_json;
                JsonElement profiles;
                if (root.TryGetProperty("profiles", out profiles) && profiles.ValueKind == JsonValueKind.Array)
                    target.profiles = JsonSerializer.Deserialize<List<GameProfileConfig>>(profiles.GetRawText(), JsonOptions)
                        ?? target.profiles;
            }
            if (!string.IsNullOrWhiteSpace(target.profiles_json))
            {
                try
                {
                    target.profiles = JsonSerializer.Deserialize<List<GameProfileConfig>>(
                        target.profiles_json, JsonOptions) ?? target.profiles;
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException("游戏档案 JSON 无效：" + exception.Message);
                }
            }
        }

        private static void Normalize(GameSessionConfig value)
        {
            value.websocket_path = (value.websocket_path ?? string.Empty).Trim();
            if (!value.websocket_path.StartsWith("/", StringComparison.Ordinal))
                value.websocket_path = "/" + value.websocket_path;
            if (value.websocket_path.Length < 2) value.websocket_path = "/plugins/game-session/ws";
            value.summary_event_count = Clamp(value.summary_event_count, 5, 500);
            value.summary_char_count = Clamp(value.summary_char_count, 500, 100000);
            value.summary_idle_minutes = Clamp(value.summary_idle_minutes, 1, 240);
            value.sync_mode = NormalizeSync(value.sync_mode);
            value.sync_interval_minutes = Clamp(value.sync_interval_minutes, 5, 1440);
            value.session_timeout_minutes = Clamp(value.session_timeout_minutes, 15, 2880);
            value.facet_max_chars = Clamp(value.facet_max_chars, 300, 4000);
            value.profiles = (value.profiles ?? new List<GameProfileConfig>())
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.id))
                .GroupBy(x => x.id.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(x => NormalizeProfile(x.Last(), value))
                .ToList();
            if (value.profiles.Count == 0)
                value.profiles.Add(NormalizeProfile(new GameProfileConfig(), value));
        }

        private static GameProfileConfig NormalizeProfile(GameProfileConfig profile, GameSessionConfig parent)
        {
            profile.id = (profile.id ?? "generic").Trim();
            profile.title = string.IsNullOrWhiteSpace(profile.title) ? profile.id : profile.title.Trim();
            profile.adapter_id = string.IsNullOrWhiteSpace(profile.adapter_id) ? profile.id : profile.adapter_id.Trim();
            profile.identity_slots = (profile.identity_slots ?? new List<string>())
                .Where(IdentityCardSlotValues.IsKnown).Distinct(StringComparer.Ordinal).ToList();
            if (profile.identity_slots.Count == 0)
                profile.identity_slots.AddRange(new[] { IdentityCardSlotValues.Personality, IdentityCardSlotValues.Self });
            profile.identity_budget_chars = Clamp(profile.identity_budget_chars, 300, 6000);
            profile.role_instruction = (profile.role_instruction ?? string.Empty).Trim();
            profile.sync_mode = string.IsNullOrWhiteSpace(profile.sync_mode)
                ? parent.sync_mode : NormalizeSync(profile.sync_mode);
            profile.sync_interval_minutes = profile.sync_interval_minutes <= 0
                ? parent.sync_interval_minutes : Clamp(profile.sync_interval_minutes, 5, 1440);
            profile.session_timeout_minutes = profile.session_timeout_minutes <= 0
                ? parent.session_timeout_minutes : Clamp(profile.session_timeout_minutes, 15, 2880);
            return profile;
        }

        private static string NormalizeSync(string value)
        {
            return string.Equals((value ?? string.Empty).Trim(), "end_only", StringComparison.OrdinalIgnoreCase)
                ? "end_only" : "timed";
        }

        private static string String(JsonElement root, string name)
        {
            JsonElement value;
            if (!root.TryGetProperty(name, out value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }

        private static int Int(JsonElement root, string name, int fallback)
        {
            JsonElement value;
            int parsed;
            if (!root.TryGetProperty(name, out value)) return fallback;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out parsed)) return parsed;
            return int.TryParse(value.ToString(), out parsed) ? parsed : fallback;
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
