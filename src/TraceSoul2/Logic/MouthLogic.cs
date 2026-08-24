using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Plugins;
using TraceSoul2.Util;

namespace TraceSoul2.Logic
{
    /// <summary>
    /// 身体路由：跨层近的压过远的；同层才打分。控制台是最低的文字壳。
    /// 说话才移动激活的身体。缺的器官才往更远的身体下滑。模型只负责说什么，不挑通道。
    /// </summary>
    public static class MouthLogic
    {
        public const string FileName = "bodies.json";
        public const string LegacyFileName = "mouths.json";
        public const int DefaultScore = 50;

        public static bool IsProtocolFacet(string facetId)
        {
            if (string.IsNullOrWhiteSpace(facetId)) return true;
            if (facetId.EndsWith(".usage", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(facetId, "senses.catalog", StringComparison.Ordinal)) return true;
            return string.Equals(facetId, "qq.reply.channel", StringComparison.Ordinal);
        }

        public static string BodyOf(TraceContributionDescriptorData item)
        {
            if (item != null && !string.IsNullOrWhiteSpace(item.BodyId))
                return item.BodyId.Trim();
            return BodyOfPlugin(item == null ? string.Empty : item.PluginId, item == null ? string.Empty : item.Id);
        }

        public static string BodyOfPlugin(string pluginId, string contributionId = null)
        {
            pluginId = pluginId ?? string.Empty;
            var id = contributionId ?? string.Empty;
            if (string.Equals(pluginId, "builtin.dialogue", StringComparison.Ordinal) ||
                string.Equals(pluginId, BodyIds.Console, StringComparison.Ordinal))
                return BodyIds.Console;
            if (string.Equals(pluginId, "builtin.onebot", StringComparison.Ordinal) ||
                string.Equals(pluginId, BodyIds.Qq, StringComparison.Ordinal) ||
                pluginId.StartsWith("qq.", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("qq.", StringComparison.OrdinalIgnoreCase))
                return BodyIds.Qq;
            return string.IsNullOrWhiteSpace(pluginId) ? string.Empty : pluginId;
        }

        public static string TierOf(TraceContributionDescriptorData item)
        {
            if (item != null && !string.IsNullOrWhiteSpace(item.BodyTier))
                return item.BodyTier.Trim();
            return TierOfBody(BodyOf(item));
        }

        public static string TierOfBody(string bodyId)
        {
            if (string.Equals(bodyId, BodyIds.Console, StringComparison.Ordinal)) return BodyTierValues.Shell;
            if (string.Equals(bodyId, BodyIds.Qq, StringComparison.Ordinal)) return BodyTierValues.Chat;
            return BodyTierValues.Chat;
        }

        public static string OrganOf(TraceContributionDescriptorData item)
        {
            if (item != null && !string.IsNullOrWhiteSpace(item.Organ))
                return item.Organ.Trim();
            var provides = item == null ? string.Empty : (item.Provides ?? string.Empty).ToLowerInvariant();
            var id = item == null ? string.Empty : (item.Id ?? string.Empty).ToLowerInvariant();
            if (provides.IndexOf("sticker", StringComparison.Ordinal) >= 0 ||
                id.IndexOf("sticker", StringComparison.Ordinal) >= 0)
                return BodyOrganValues.Sticker;
            if (provides.IndexOf("qzone", StringComparison.Ordinal) >= 0 ||
                id.IndexOf("qzone", StringComparison.Ordinal) >= 0)
                return BodyOrganValues.Qzone;
            if (provides.IndexOf("tts", StringComparison.Ordinal) >= 0 ||
                provides.IndexOf("voice", StringComparison.Ordinal) >= 0 ||
                id.IndexOf("voice", StringComparison.Ordinal) >= 0 ||
                id.IndexOf("tts", StringComparison.Ordinal) >= 0)
                return BodyOrganValues.Voice;
            if (provides.IndexOf("video", StringComparison.Ordinal) >= 0 ||
                id.IndexOf("video", StringComparison.Ordinal) >= 0)
                return BodyOrganValues.Video;
            if (provides.IndexOf("image", StringComparison.Ordinal) >= 0 ||
                id.IndexOf("image", StringComparison.Ordinal) >= 0)
                return BodyOrganValues.Image;
            if (provides.IndexOf("text", StringComparison.Ordinal) >= 0 ||
                id.IndexOf("text", StringComparison.Ordinal) >= 0 ||
                string.Equals(id, "dialogue.send", StringComparison.Ordinal) ||
                string.Equals(id, "dialogue.receive", StringComparison.Ordinal))
                return BodyOrganValues.Text;
            return string.Empty;
        }

        public static string ClassifyInboundOrgan(string content)
        {
            content = content ?? string.Empty;
            var stripped = content
                .Replace("[图片]", string.Empty)
                .Replace("[表情]", string.Empty)
                .Replace("[语音]", string.Empty)
                .Replace("[视频]", string.Empty)
                .Replace("[文件]", string.Empty)
                .Replace("[回复]", string.Empty)
                .Replace("[@]", string.Empty);
            if (stripped.Trim().Length > 0) return BodyOrganValues.Text;
            if (content.IndexOf("[视频]", StringComparison.Ordinal) >= 0) return BodyOrganValues.Video;
            if (content.IndexOf("[语音]", StringComparison.Ordinal) >= 0) return BodyOrganValues.Voice;
            if (content.IndexOf("[图片]", StringComparison.Ordinal) >= 0 ||
                content.IndexOf("[表情]", StringComparison.Ordinal) >= 0)
                return BodyOrganValues.Image;
            return BodyOrganValues.Text;
        }

        public static bool IsBodyLive(string body, TraceTurnContext turn)
        {
            if (string.Equals(body, BodyIds.Console, StringComparison.Ordinal)) return true;
            if (turn == null || turn.Services == null || turn.Services.Platforms == null)
                return false;
            var handle = turn.Services.Platforms.List().FirstOrDefault(x =>
                x != null && string.Equals(x.Id, body, StringComparison.OrdinalIgnoreCase));
            if (handle == null || handle.IsConnected == null) return false;
            try { return handle.IsConnected(); }
            catch { return false; }
        }

        /// <summary>说话才改激活的身体；图/视频只在还没有激活身体时冷启动。</summary>
        public static void NoticeInbound(PluginEventData source, TraceTurnContext turn)
        {
            if (source == null || turn == null || turn.Services == null) return;
            var body = BodyOfPlugin(source.PluginId);
            if (string.IsNullOrWhiteSpace(body)) return;
            var organ = string.IsNullOrWhiteSpace(source.Organ)
                ? ClassifyInboundOrgan(source.Content)
                : source.Organ.Trim();
            var state = LoadState(turn.Services.DataDirectory);
            if (BodyOrganValues.IsSpeak(organ) && IsBodyLive(body, turn))
            {
                state.active_body = body;
                SaveState(turn.Services.DataDirectory, state);
                return;
            }
            if (string.IsNullOrWhiteSpace(state.active_body) && IsBodyLive(body, turn))
            {
                state.active_body = body;
                SaveState(turn.Services.DataDirectory, state);
            }
        }

        public static List<TraceContributionDescriptorData> Apply(
            IEnumerable<TraceContributionDescriptorData> catalog,
            TraceTurnContext turn)
        {
            var source = (catalog ?? Enumerable.Empty<TraceContributionDescriptorData>())
                .Where(x => x != null && !IsProtocolFacet(x.Id))
                .ToList();
            var rest = source.Where(x => x.Kind != TraceContributionKindValues.Effector).ToList();
            var effectors = source.Where(x => x.Kind == TraceContributionKindValues.Effector).ToList();
            var winners = new List<TraceContributionDescriptorData>();
            foreach (var group in effectors.GroupBy(OrganOf, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(group.Key))
                {
                    winners.AddRange(group);
                    continue;
                }
                var chosen = RouteEffector(group.Key, group, turn);
                if (chosen != null) winners.Add(chosen);
            }
            return rest.Concat(winners)
                .OrderBy(x => x.Kind, StringComparer.Ordinal)
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .ToList();
        }

        public static string WinningTextChannel(IEnumerable<TraceContributionDescriptorData> catalog)
        {
            var text = (catalog ?? Enumerable.Empty<TraceContributionDescriptorData>())
                .FirstOrDefault(x => x != null &&
                                     x.Kind == TraceContributionKindValues.Effector &&
                                     string.Equals(OrganOf(x), BodyOrganValues.Text, StringComparison.Ordinal));
            return text == null ? null : text.Id;
        }

        public static TraceContributionDescriptorData RouteEffector(
            string organ,
            IEnumerable<TraceContributionDescriptorData> candidates,
            TraceTurnContext turn)
        {
            var live = (candidates ?? Enumerable.Empty<TraceContributionDescriptorData>())
                .Where(x => x != null &&
                            string.Equals(OrganOf(x), organ, StringComparison.Ordinal) &&
                            IsBodyLive(BodyOf(x), turn))
                .ToList();
            if (live.Count == 0) return null;
            var state = LoadState(turn == null || turn.Services == null ? null : turn.Services.DataDirectory);
            var active = (state.active_body ?? string.Empty).Trim();
            var onActive = live.Where(x => string.Equals(BodyOf(x), active, StringComparison.Ordinal)).ToList();
            if (onActive.Count > 0) return PickSameBody(onActive, organ);
            var winningBody = live
                .OrderByDescending(x => BodyTierValues.Nearness(TierOf(x)))
                .ThenByDescending(x => ScaleBias(x, state.scene))
                .ThenByDescending(x => ScoreOf(BodyOf(x), state))
                .ThenBy(x => BodyOf(x), StringComparer.Ordinal)
                .ThenBy(x => x.Id, StringComparer.Ordinal)
                .Select(BodyOf)
                .First();
            return PickSameBody(live.Where(x => string.Equals(BodyOf(x), winningBody, StringComparison.Ordinal)), organ);
        }

        public static IReadOnlyCollection<string> ExtraModalities(
            IEnumerable<TraceContributionDescriptorData> catalog)
        {
            return new HashSet<string>(
                (catalog ?? Enumerable.Empty<TraceContributionDescriptorData>())
                    .Where(x => x != null && x.Kind == TraceContributionKindValues.Effector)
                    .Select(OrganOf)
                    .Where(x => x == BodyOrganValues.Sticker ||
                                x == BodyOrganValues.Qzone ||
                                x == BodyOrganValues.Voice ||
                                x == BodyOrganValues.Image ||
                                x == BodyOrganValues.Video),
                StringComparer.Ordinal);
        }

        public static object Describe(string dataDirectory, TracePluginServices services)
        {
            var state = LoadState(dataDirectory);
            var platforms = services == null || services.Platforms == null
                ? new List<PlatformHandle>()
                : services.Platforms.List();
            var catalog = services != null && services.EnabledCatalogProvider != null
                ? services.EnabledCatalogProvider()
                : new List<TraceContributionDescriptorData>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                BodyIds.Console,
                BodyIds.Qq
            };
            foreach (var item in state.items ?? new BodyScoreEntry[0])
                if (item != null && !string.IsNullOrWhiteSpace(item.id))
                    ids.Add(item.id);
            foreach (var platform in platforms)
                if (platform != null && !string.IsNullOrWhiteSpace(platform.Id))
                    ids.Add(platform.Id);
            var dummy = DummyTurn(services);
            var available = services != null && services.AvailableCatalogProvider != null
                ? services.AvailableCatalogProvider(dummy)
                : catalog;
            var availableIds = new HashSet<string>(
                (available ?? new List<TraceContributionDescriptorData>())
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id))
                    .Select(x => x.Id),
                StringComparer.Ordinal);
            var bodies = ids.OrderByDescending(x => BodyTierValues.Nearness(TierOfBody(x)))
                .ThenByDescending(x => ScoreOf(x, state))
                .ThenBy(x => x, StringComparer.Ordinal)
                .Select(id =>
                {
                    var handle = platforms.FirstOrDefault(x =>
                        x != null && string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                    var connected = IsBodyLive(id, dummy);
                    return new
                    {
                        id,
                        displayName = handle == null ? LabelOf(id) : handle.DisplayName,
                        tier = TierOfBody(id),
                        score = ScoreOf(id, state),
                        connected,
                        active = string.Equals(id, state.active_body, StringComparison.Ordinal),
                        organs = OrgansOf(id, catalog, availableIds)
                    };
                })
                .ToList();
            return new
            {
                scene = string.IsNullOrWhiteSpace(state.scene) ? "home" : state.scene,
                activeBody = state.active_body ?? string.Empty,
                bodies
            };
        }

        public static void SaveRanks(string dataDirectory, IEnumerable<MouthRankEntry> items)
        {
            var state = LoadState(dataDirectory);
            foreach (var item in items ?? Enumerable.Empty<MouthRankEntry>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.id)) continue;
                UpsertScore(state, item.id.Trim(), Clamp(item.priority));
            }
            SaveState(dataDirectory, state);
        }

        public static void SaveRouting(string dataDirectory, string scene, string activeBody,
            IEnumerable<MouthRankEntry> items)
        {
            var state = LoadState(dataDirectory);
            if (!string.IsNullOrWhiteSpace(scene))
                state.scene = BodySceneValues.Normalize(scene);
            if (activeBody != null)
                state.active_body = activeBody.Trim();
            foreach (var item in items ?? Enumerable.Empty<MouthRankEntry>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.id)) continue;
                UpsertScore(state, item.id.Trim(), Clamp(item.priority));
            }
            SaveState(dataDirectory, state);
        }

        /// <summary>更新物理所在场景，不改变身体分数或当前激活身体。</summary>
        public static void SetScene(string dataDirectory, string scene)
        {
            var state = LoadState(dataDirectory);
            state.scene = BodySceneValues.Normalize(scene);
            SaveState(dataDirectory, state);
        }

        private static TraceContributionDescriptorData PickSameBody(
            IEnumerable<TraceContributionDescriptorData> items,
            string organ)
        {
            var values = (items ?? Enumerable.Empty<TraceContributionDescriptorData>()).ToList();
            if (string.Equals(organ, BodyOrganValues.Image, StringComparison.Ordinal))
            {
                var producer = values
                    .Where(IsPromptImageEffector)
                    .OrderByDescending(x =>
                        (x.Id ?? string.Empty).IndexOf("imagegen", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ThenBy(x => x.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (producer != null) return producer;
            }
            return values.OrderBy(x => x.Id, StringComparer.Ordinal).First();
        }

        private static bool IsPromptImageEffector(TraceContributionDescriptorData item)
        {
            if (item == null) return false;
            var schema = item.ParametersJsonSchema ?? string.Empty;
            return schema.IndexOf("prompt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   schema.IndexOf("url", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (item.Id ?? string.Empty).IndexOf("imagegen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (item.Provides ?? string.Empty).IndexOf("imagegen", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int ScaleBias(TraceContributionDescriptorData item, string scene)
        {
            var scale = item == null ? string.Empty : (item.BodyScale ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(scale)) return 0;
            var atHome = !string.Equals(scene, "out", StringComparison.OrdinalIgnoreCase);
            if (atHome)
            {
                if (scale == BodyScaleValues.Life) return 30;
                if (scale == BodyScaleValues.Large) return 20;
                if (scale == BodyScaleValues.Small) return 5;
                return 0;
            }
            if (scale == BodyScaleValues.Small) return 30;
            if (scale == BodyScaleValues.Large) return 10;
            if (scale == BodyScaleValues.Life) return 0;
            return 0;
        }

        private static int ScoreOf(string bodyId, BodyRoutingState state)
        {
            var found = (state.items ?? new BodyScoreEntry[0]).FirstOrDefault(x =>
                x != null && string.Equals(x.id, bodyId, StringComparison.OrdinalIgnoreCase));
            return found == null ? DefaultScore : Clamp(found.score != 0 ? found.score : found.priority);
        }

        private static void UpsertScore(BodyRoutingState state, string id, int score)
        {
            var list = (state.items ?? new BodyScoreEntry[0]).ToList();
            var found = list.FirstOrDefault(x =>
                x != null && string.Equals(x.id, id, StringComparison.OrdinalIgnoreCase));
            if (found == null)
                list.Add(new BodyScoreEntry { id = id, score = score });
            else
                found.score = score;
            state.items = list.ToArray();
        }

        public static BodyRoutingState LoadState(string dataDirectory)
        {
            var state = new BodyRoutingState
            {
                active_body = string.Empty,
                scene = BodySceneValues.Home,
                items = new[]
                {
                    new BodyScoreEntry { id = BodyIds.Console, score = DefaultScore },
                    new BodyScoreEntry { id = BodyIds.Qq, score = DefaultScore }
                }
            };
            if (string.IsNullOrWhiteSpace(dataDirectory)) return state;
            try
            {
                var path = Path.Combine(dataDirectory, FileName);
                if (!File.Exists(path))
                    path = Path.Combine(dataDirectory, LegacyFileName);
                if (!File.Exists(path)) return state;
                var loaded = TraceJson.FromJson<BodyRoutingState>(File.ReadAllText(path));
                if (loaded == null) return state;
                if (!string.IsNullOrWhiteSpace(loaded.active_body))
                    state.active_body = loaded.active_body.Trim();
                if (!string.IsNullOrWhiteSpace(loaded.scene))
                    state.scene = BodySceneValues.Normalize(loaded.scene);
                if (loaded.items != null)
                {
                    foreach (var item in loaded.items)
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.id)) continue;
                        var score = item.score != 0 ? item.score : item.priority;
                        UpsertScore(state, item.id.Trim(), Clamp(score == 0 ? DefaultScore : score));
                    }
                }
            }
            catch { /* 配置损坏按默认 */ }
            return state;
        }

        private static void SaveState(string dataDirectory, BodyRoutingState state)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory) || state == null) return;
            Directory.CreateDirectory(dataDirectory);
            var document = new BodyRoutingState
            {
                active_body = state.active_body ?? string.Empty,
                scene = BodySceneValues.Normalize(state.scene),
                items = (state.items ?? new BodyScoreEntry[0])
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.id))
                    .OrderByDescending(x => x.score)
                    .ThenBy(x => x.id, StringComparer.Ordinal)
                    .ToArray()
            };
            File.WriteAllText(Path.Combine(dataDirectory, FileName), TraceJson.ToJson(document));
        }

        private static object OrgansOf(
            string bodyId,
            List<TraceContributionDescriptorData> catalog,
            HashSet<string> availableIds)
        {
            return (catalog ?? new List<TraceContributionDescriptorData>())
                .Where(x => x != null &&
                            x.Kind == TraceContributionKindValues.Effector &&
                            !IsProtocolFacet(x.Id) &&
                            string.Equals(BodyOf(x), bodyId, StringComparison.Ordinal))
                .GroupBy(OrganOf, StringComparer.Ordinal)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var item = PickSameBody(g, g.Key);
                    var ready = availableIds != null &&
                                g.Any(x => x != null && availableIds.Contains(x.Id));
                    return new
                    {
                        organ = g.Key,
                        id = item.Id,
                        displayName = string.IsNullOrWhiteSpace(item.DisplayName)
                            ? OrganLabel(g.Key) : item.DisplayName,
                        ready,
                        blocked = ready ? string.Empty : "未就绪"
                    };
                })
                .ToList();
        }

        private static string OrganLabel(string organ)
        {
            if (organ == BodyOrganValues.Text) return "文字";
            if (organ == BodyOrganValues.Image) return "图";
            if (organ == BodyOrganValues.Sticker) return "表情";
            if (organ == BodyOrganValues.Voice) return "语音";
            if (organ == BodyOrganValues.Video) return "视频";
            if (organ == BodyOrganValues.Qzone) return "说说";
            return organ;
        }

        private static TraceTurnContext DummyTurn(TracePluginServices services)
        {
            return new TraceTurnContext("status", new MomentRecord(), new List<MomentRecord>(),
                0, false, services);
        }

        private static string LabelOf(string id)
        {
            if (string.Equals(id, BodyIds.Qq, StringComparison.OrdinalIgnoreCase)) return "QQ";
            if (string.Equals(id, BodyIds.Console, StringComparison.OrdinalIgnoreCase)) return "控制台";
            return id;
        }

        private static int Clamp(int value)
        {
            return Math.Max(0, Math.Min(100, value));
        }
    }

    [Serializable]
    public sealed class BodyRoutingState
    {
        public string active_body = string.Empty;
        public string scene = "home";
        public BodyScoreEntry[] items = new BodyScoreEntry[0];
    }

    [Serializable]
    public sealed class BodyScoreEntry
    {
        public string id;
        public int score;
        public int priority;
    }

    [Serializable]
    public sealed class MouthRankEntry
    {
        public string id;
        public int priority;
    }
}
