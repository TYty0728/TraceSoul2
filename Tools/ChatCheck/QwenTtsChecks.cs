using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Host;

internal static partial class Program
{
    private sealed class QwenModelProbe : HttpMessageHandler
    {
        public string Url, Key;
        public int Count;
        public HttpStatusCode Code = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Url = request.RequestUri.AbsoluteUri; Key = request.Headers.Authorization?.Parameter; Count++;
            return Task.FromResult(new HttpResponseMessage(Code) { Content = new StringContent(
                Code == HttpStatusCode.OK ? "{\"data\":[{\"id\":\"qwen3-tts-local\"}]}" : "do-not-echo-provider-body") });
        }
    }
    private static async Task RunQwenTtsChecksAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "qwen-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "providers.json");
            var store = new LlmProviderStore(path);
            var original = store.CurrentId;
            var provider = store.AddFromTemplate("qwen-local-tts", null);
            Require(provider.voice == "character" && provider.models.Single().roles.SequenceEqual(new[] { "speech" }) &&
                provider.transientRetries == 0 && store.CurrentId == original, "千问模板只用于语音，不更改主模型");
            provider.apiKey = new string('Q', 64);
            provider.voice = "demo";
            store.Upsert(provider);
            void Reject(Action action, string message)
            {
                var failed = false;
                try { action(); } catch (InvalidOperationException) { failed = true; }
                Require(failed, message);
            }
            Reject(() => store.Select(provider.id, provider.model), "TTS不能作为主模型");
            foreach (var slot in new[] { "chat", "review", "jev", "image", "multimodal" })
                Reject(() => store.SetSlot(slot, provider.id, provider.model), "TTS拒绝非语音槽 " + slot);
            Reject(() => store.CreateClient(provider.id), "TTS拒绝聊天客户端");
            store.SetSlot("speech", provider.id, provider.model);
            store.UpsertModel(provider.id, provider.model, true, new System.Collections.Generic.List<string> { "chat", "image" });
            store = new LlmProviderStore(path);
            Require(store.SpeechProvider().voice == "demo" && store.Get(provider.id).models.Single().roles.SequenceEqual(new[] { "speech" }),
                "重载保留音色、密钥、语音槽，强制语音用途");
            var fetched = new QwenModelProbe();
            var models = await QwenTtsProvider.ListModelsAsync(store.SpeechProvider(), CancellationToken.None, fetched);
            Require(models.Single() == provider.model && fetched.Url == "http://127.0.0.1:5088/v1/models" && fetched.Key == provider.apiKey,
                "获取模型列表使用正确鉴权，不走聊天协议");
            fetched = new QwenModelProbe { Code = HttpStatusCode.Unauthorized };
            try { await QwenTtsProvider.ListModelsAsync(provider, CancellationToken.None, fetched); throw new Exception("401 accepted"); }
            catch (InvalidOperationException e) { Require(fetched.Count == 1 && e.Message.Contains("401") && !e.Message.Contains("do-not-echo"), "模型获取失败脱敏、不重试"); }
            SpeechTransport transport = null;
            var speech = WxxzSpeech.ForProvider(() => store.SpeechProvider(), () => transport = new SpeechTransport());
            await speech.SynthesizeAsync("供应商测试", CancellationToken.None);
            Require(transport.Key == provider.apiKey && transport.Model == provider.model && transport.Voice == "demo", "WXXZ由语音供应商确定地址密钥模型音色");
            var updated = store.Get(provider.id); updated.voice = "character"; store.Upsert(updated);
            await speech.SynthesizeAsync("更新后测试", CancellationToken.None);
            Require(transport.Voice == "character", "供应商修改后下一次合成立即采用，无需重启");
            Require(!JsonSerializer.Serialize(speech.Public()).Contains(provider.apiKey), "公开语音状态不回显密钥");
            var link = await speech.WorkbenchAsync(CancellationToken.None);
            Require(link.StartsWith("http://127.0.0.1:5088/auth/launch?ticket=") && !link.Contains(provider.apiKey), "工作台沿用供应商独立Key，只返回短期票据");
            store.UpsertModel(provider.id, provider.model, false, null);
            Reject(() => store.SpeechProvider(), "已停用语音模型不能继续调用");
            store.SetSlot("speech", "", "");
            Reject(() => store.SpeechProvider(), "清空槽后不回退旧配置或其他供应商");
            foreach (var url in new[] { "http://external.example/v1", "https://example/v1?key=secret", "https://" + "u:p@" + "example/v1", "http://127.0.0.1:5088/v1/audio/speech" })
            {
                updated = store.Get(provider.id); updated.baseUrl = url;
                Reject(() => store.Upsert(updated), "不安全或错误基址拒绝保存");
                Require(store.Get(provider.id).baseUrl == provider.baseUrl, "保存失败保留原配置");
            }
            var stale = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            stale["multimodal"] = new System.Text.Json.Nodes.JsonObject { ["providerId"] = provider.id, ["model"] = provider.model };
            File.WriteAllText(path, stale.ToJsonString());
            var staleStore = new LlmProviderStore(path);
            Reject(() => staleStore.ResolveSlot("multimodal"), "旧文件中错误的TTS识图槽也拒绝解析");
            Reject(() => staleStore.ResolveExplicitSlot("multimodal"), "显式解析同样拒绝TTS进入非语音槽");
            var legacy = Path.Combine(folder, "local-config.json");
            File.WriteAllText(legacy, JsonSerializer.Serialize(new { api_url = "http://127.0.0.1:5088/v1/audio/speech", api_key = new string('M', 64) }));
            var imported = new LlmProviderStore(Path.Combine(folder, "imported.json"));
            var before = imported.CurrentId;
            imported.ImportLocalVoice(legacy);
            Require(imported.SpeechProvider().apiKey == new string('M', 64) && imported.CurrentId == before, "现有私密配置自动导入，不改主模型");
            var id = imported.SpeechProvider().id;
            var edit = imported.Get(id); edit.voice = "demo"; imported.Upsert(edit);
            imported = new LlmProviderStore(Path.Combine(folder, "imported.json")); imported.ImportLocalVoice(legacy);
            Require(imported.SpeechProvider().voice == "demo" && imported.List().Count(x => LlmProviderCatalog.IsTts(x.type)) == 1, "迁移仅一次，不覆盖用户修改或重复添加");
            imported.Delete(id); imported.ImportLocalVoice(legacy);
            Require(!imported.List().Any(x => LlmProviderCatalog.IsTts(x.type)), "删除后不从旧配置自动复活");
            Console.WriteLine("Qwen TTS provider checks passed: migration, speech-only routing, credentials, model discovery, preview and no fallback.");
        }
        finally { Directory.Delete(folder, true); }
    }
}
