using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TraceSoul2.Host;

internal static partial class Program
{
    private static async Task RunModelSyncChecksAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "model-sync-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "providers.json");
            var store = MakeFallbackStore(path, ("primary", "https://primary.invalid", null),
                ("secondary", "https://secondary.invalid", null));
            store.UpsertModel("secondary", "kept", false, new List<string> { "multimodal" });
            foreach (var slot in new[] { "jev", "review", "multimodal", "image", "speech" })
                store.SetSlot(slot, "secondary", "secondary-model");
            var primaryBefore = JsonSerializer.Serialize(store.Get("primary"));
            var synced = store.MergeFetched("secondary", new[] { " kept ", "kept", "new-chat", "gpt-image-test" });
            Require(synced.models.Count == 3 && synced.models.All(x => x.id != "secondary-model"), "同步去重并删除失效模型");
            Require(!synced.models.Single(x => x.id == "kept").enabled &&
                synced.models.Single(x => x.id == "kept").roles.SequenceEqual(new[] { "multimodal" }), "保留现有模型设置");
            Require(synced.model == "new-chat", "默认失效时选择仍可用的聊天模型");
            Require(store.Slots().Where(x => x.Key != "chat").All(x => string.IsNullOrEmpty(x.Value.providerId)) &&
                string.IsNullOrEmpty(store.MainFallbacks().secondary.providerId), "清理用途和备用引用");
            Require(JsonSerializer.Serialize(store.Get("primary")) == primaryBefore, "其他供应商不受影响");
            store = new LlmProviderStore(path);
            Require(store.Get("secondary").models.Count == 3, "同步结果持久化");
            store.MergeFetched("secondary", Array.Empty<string>());
            store = new LlmProviderStore(path);
            Require(store.Get("secondary").models.Count == 0 && string.IsNullOrEmpty(store.Get("secondary").model), "成功空列表不会被旧默认模型重新填充");

            foreach (var native in new[] { false, true })
            foreach (var response in new[] { (200, "{}"), (200, "{\"error\":{\"message\":\"failed\"}}"),
                (503, "{}"), (200, native ? "{\"models\":[{}]}" : "{\"data\":[{}]}"),
                (200, native ? "{\"models\":[{\"name\":\"models/one\"}],\"nextPageToken\":\"repeat\"}" :
                    "{\"data\":[{\"id\":\"one\"}],\"has_more\":true}") })
            {
                using var server = new FailureHttpServer(response.Item1, response.Item2);
                var probe = MakeFallbackStore(Path.Combine(root, Guid.NewGuid()+".json"),
                    ("probe", server.Url, native ? LlmProviderCatalog.GoogleGenAi : null));
                var before = JsonSerializer.Serialize(probe.Get("probe"));
                await ExpectFailureAsync(async () => JsonSerializer.Serialize(
                    probe.MergeFetched("probe", await probe.CreateClient("probe").ListModelsAsync())));
                Require(JsonSerializer.Serialize(probe.Get("probe")) == before, "失败或不完整列表保留原配置");
            }
            using (var server = new FailureHttpServer(200, "", call => call == 1
                ? (200, "{\"models\":[{\"name\":\"models/first\"}],\"nextPageToken\":\"page2\"}")
                : (200, "{\"models\":[{\"name\":\"models/second\"}]}")))
            {
                var probe = MakeFallbackStore(Path.Combine(root, "pages.json"), ("probe", server.Url, LlmProviderCatalog.GoogleGenAi));
                var fetched = await probe.CreateClient("probe").ListModelsAsync();
                Require(fetched.SequenceEqual(new[] { "first", "second" }) && server.Calls == 2, "Gemini完整读取分页后同步");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("Model sync checks passed: prune, preserve settings, references, reload, empty, failures, pagination.");
    }
}
