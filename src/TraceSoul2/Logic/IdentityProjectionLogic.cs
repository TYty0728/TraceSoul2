using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TraceSoul2.Data;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

namespace TraceSoul2.Logic
{
    /// <summary>同一身份依据的稳定视图。卡片与认知共享出处，失效依据不继续冒充当前自我。</summary>
    public static class IdentityProjectionLogic
    {
        public static bool Current(IdentityCardRecord card, IEnumerable<CognitionSliceRecord> nodes)
        {
            if (card.Origin != "derived") return true;
            var sources = PuzzleViewLogic.Read<Dictionary<string, string>>(card.CognitionSourcesJson);
            if (sources == null || sources.Count == 0) return false;
            var index = nodes.ToDictionary(x => x.Id);
            return sources.All(x => index.TryGetValue(x.Key, out var node) && node.Status == "active" &&
                PuzzleViewLogic.Stamp(node) == x.Value);
        }

        public static string Build(TraceTurnContext turn)
        {
            var store = turn.Services.Storage;
            var pair = store.LoadPairIdentity();
            var cards = store.LoadIdentityCards(PuzzleViewLogic.Root(turn));
            var nodes = (store as ICognitionGraphStore)?.GetCognitionNodes(5000) ?? new();
            var visible = nodes.Where(x => PuzzleViewLogic.CanRead(turn, x)).ToList();
            var isPublic = EnvironmentLogic.IsPublic(turn);
            var builder = new StringBuilder("我是" + pair.Assname + "。\n");
            foreach (var card in cards)
            {
                if (!Current(card, nodes)) continue;
                if (isPublic && !PuzzleViewLogic.Shared(store, card.Id, PuzzleViewLogic.Stamp(card))) continue;
                var body = IdentityCardLogic.ResolveBody(card.Slot, card.Body, pair);
                var title = isPublic && card.Slot is "other" or "user_profile" ? "关于专属用户" :
                    isPublic && card.Slot == "relation" ? "与专属用户的关系" : IdentityCardSlotValues.Title(card.Slot, pair);
                builder.Append('【').Append(title).Append("】").AppendLine(body);
            }
            // 兼容旧配置：这份本人确认的共享自我描述同时用于私密和公开，绝不另选一份人格。
            var shared = EnvironmentLogic.Settings(store).PublicIdentity;
            if (!string.IsNullOrWhiteSpace(shared)) builder.AppendLine("【跨环境自我描述（本人确认）】" + shared.Trim());
            var pinned = cards.Where(x => x.Pinned).Select(x => x.Slot).ToHashSet();
            var covered = cards.Where(x => Current(x, nodes) && (!isPublic || PuzzleViewLogic.Shared(store, x.Id, PuzzleViewLogic.Stamp(x))))
                .SelectMany(x => (PuzzleViewLogic.Read<Dictionary<string, string>>(x.CognitionSourcesJson) ?? new()).Keys).ToHashSet();
            var chars = 0;
            foreach (var node in visible.Where(x => x.Status == "active" && !string.IsNullOrEmpty(x.IdentitySlot) &&
                         !pinned.Contains(x.IdentitySlot) && !covered.Contains(x.Id))
                .OrderByDescending(x => x.Strength).ThenBy(x => x.Id, StringComparer.Ordinal).Take(12))
            {
                var text = "【经历形成的理解：" + node.IdentitySlot + "】" + node.Summary +
                    (node.IdentitySlot is "other" or "relation" ? "；对象：" + node.About + "（" + node.SubjectKey + "）" : "") +
                    (string.IsNullOrWhiteSpace(node.Scope) ? "" : "；适用：" + node.Scope) +
                    (string.IsNullOrWhiteSpace(node.Exceptions) ? "" : "；例外：" + node.Exceptions);
                if (chars + text.Length > 3000) continue;
                chars += text.Length;
                builder.AppendLine(text);
            }
            if (isPublic) builder.AppendLine("我保持同一个自我；当前在场者不自动具有与专属用户相同的关系。未提供的私人依据不能补写或推测。");
            return builder.ToString().TrimEnd();
        }
    }
}
