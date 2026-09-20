using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.ExternalPlugins;
using TraceSoul2.Logic;
using TraceSoul2.Manager;
using TraceSoul2.Plugins;

internal static partial class Program
{
    private static async Task RunQzoneMediaChecksAsync()
    {
        const string upload = "callback({\"code\":0,\"data\":{\"albumid\":\"album\",\"lloc\":\"large\",\"sloc\":\"small\",\"type\":1,\"width\":640,\"height\":480,\"pre\":\"https://example.test/p?bo=AB%2B%3D&other=ignored\"}});";
        var photo = QqQzonePlugin.ParseUploadedPhoto(upload);
        Require(photo.RichValue == ",album,large,small,1,480,640,,480,640" && photo.Bo == "AB+=",
            "空间上传回包应正确生成 richval，bo 不能夹带其它 URL 参数");
        var rejected = false;
        try { QqQzonePlugin.ParseUploadedPhoto("{\"code\":0,\"data\":{}}"); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "缺少上传字段时不能带着伪造图片信息发布");

        var file = Path.Combine(Path.GetTempPath(), "qzone-check-" + Guid.NewGuid().ToString("N") + ".png");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        await File.WriteAllBytesAsync(file, png);
        try
        {
            using var store = new SqliteMemoryManager(":memory:");
            store.SavePairIdentity("小雨", "小光", "雨雨");
            foreach (var mode in new[] { "text", "image", "upload-fail", "publish-uncertain", "generate-fail", "cancel", "no-camera", "existing" })
            {
                using var http = new QzoneHttpHandler(upload, mode);
                var plugin = new QqQzonePlugin(() => new HttpClient(http, false));
                var llm = new QzoneDraftLlm(mode == "text"
                    ? "{\"content\":\"完整的说说正文\",\"image_prompt\":\"\"}"
                    : "{\"content\":\"完整的说说正文\",\"image_prompt\":\"想分享手里的茶杯\"}");
                var services = new TracePluginServices(store, new HierarchicalVectorRouterLogic(new FakeEncoder())) { Llm = llm };
                services.PlatformAdapters.Add(new QzoneCookieAdapter());
                var turn = new TraceTurnContext("qzone-check", Moment("qzone-check", "发一条说说"), new List<MomentRecord>(), 0, false, services);
                var source = new BrainCapabilityCallData
                {
                    call_id = "qzone-test", capability_id = "qq.qzone.publish",
                    arguments = new List<BrainCallArgumentData>
                    {
                        new BrainCallArgumentData { name = "idle", value = "true" },
                        new BrainCallArgumentData { name = "seed", value = "正在喝茶，想起了早上的话" }
                    }
                };
                if (mode == "existing") source.arguments.Add(new BrainCallArgumentData { name = "content", value = "用户指定正文保持原样" });
                var catalog = new List<TraceContributionDescriptorData>
                {
                    new TraceContributionDescriptorData { Id = source.capability_id, ParametersJsonSchema = "{content:string,image_prompt?:string,dispatch?:prepare|publish}" }
                };
                if (mode != "no-camera") catalog.Add(new TraceContributionDescriptorData
                    { Id = "qq.imagegen.generate", ParametersJsonSchema = "{dispatch?:generate|send|release}" });
                var cameraSteps = new List<string>();
                async Task<TraceCapabilityResultData> Execute(BrainCapabilityCallData call, CancellationToken token)
                {
                    if (call.capability_id == "qq.qzone.publish")
                        return await plugin.PublishFromCallAsync(call, turn, token);
                    var dispatch = call.GetArgument("dispatch");
                    cameraSteps.Add(dispatch);
                    Require(dispatch != "send" && dispatch != "all", "空间配图不能附带向 QQ 私聊发图");
                    if (dispatch == "release")
                    {
                        Require(!token.IsCancellationRequested, "取消后仍应清理生成文件");
                        return new TraceCapabilityResultData { Status = "success" };
                    }
                    return new TraceCapabilityResultData
                    {
                        Status = mode == "generate-fail" ? "failed" : "success",
                        Payload = mode == "generate-fail" ? "" : file
                    };
                }
                var threw = false;
                TraceCapabilityResultData result = null;
                try { result = await QzonePublishLogic.ExecuteAsync(source, turn, catalog, Execute, default); }
                catch (InvalidOperationException) { threw = true; }
                catch (OperationCanceledException) { threw = true; }
                if (mode == "upload-fail" || mode == "cancel")
                {
                    Require(threw && http.PublishCount == 0 && cameraSteps.Last() == "release",
                        "上传失败或取消不能先发纯文本，且需要清理生成图片");
                    continue;
                }
                if (mode == "generate-fail")
                {
                    Require(result.Status == "failed" && http.PublishCount == 0 && http.UploadCount == 0,
                        "生图失败不能发布不完整说说");
                    continue;
                }
                Require(!threw && result.Status == "success" && http.PublishCount == 1,
                    "每条说说只能提交一次，包括接口假失败");
                if (mode == "text" || mode == "no-camera")
                    Require(cameraSteps.Count == 0 && http.UploadCount == 0 && !http.PublishForm.ContainsKey("richval"),
                        "不想配图或相机不可用时应正常发纯文字");
                else
                {
                    Require(cameraSteps.SequenceEqual(new[] { "generate", "release" }) && http.UploadCount == 1 &&
                            http.PublishForm["richval"] == photo.RichValue && http.PublishForm["pic_bo"] == "AB+=",
                        "配图应先生成上传，再和正文一次发布，最后清理文件");
                    Require(http.UploadForm["picfile"] == Convert.ToBase64String(png) && http.UploadForm["albumtype"] == "7",
                        "上传请求应包含真正的图片数据并使用说说附件类型");
                }
                if (mode == "existing") Require(http.PublishForm["con"] == "用户指定正文保持原样",
                    "决定配图时不能改写用户指定的说说正文");
            }

            var oldCalls = 0;
            await QzonePublishLogic.ExecuteAsync(new BrainCapabilityCallData { capability_id = "qq.qzone.publish" }, null,
                new[] { new TraceContributionDescriptorData { Id = "qq.qzone.publish", ParametersJsonSchema = "{content:string}" } },
                (call, _) =>
                {
                    oldCalls++;
                    Require(call.GetArgument("dispatch") != "prepare", "旧插件不能收到会被当成发布的 prepare 请求");
                    return Task.FromResult(new TraceCapabilityResultData { Status = "success" });
                }, default);
            Require(oldCalls == 1, "旧空间插件仍只发布一次");
        }
        finally { File.Delete(file); }
    }

    private sealed class QzoneHttpHandler : HttpMessageHandler
    {
        private readonly string upload;
        private readonly string mode;
        public int UploadCount;
        public int PublishCount;
        public Dictionary<string, string> UploadForm;
        public Dictionary<string, string> PublishForm;
        public QzoneHttpHandler(string upload, string mode) { this.upload = upload; this.mode = mode; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var fields = (await request.Content.ReadAsStringAsync(token)).Split('&').Select(x => x.Split('=', 2))
                .ToDictionary(x => Uri.UnescapeDataString(x[0].Replace('+', ' ')),
                    x => Uri.UnescapeDataString(x[1].Replace('+', ' ')));
            if (request.RequestUri.Host == "up.qzone.qq.com")
            {
                UploadCount++; UploadForm = fields;
                Require(PublishCount == 0, "上传必须早于发布");
                if (mode == "cancel") throw new OperationCanceledException();
                return new HttpResponseMessage(mode == "upload-fail" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
                    { Content = new StringContent(upload) };
            }
            Require(request.RequestUri.AbsolutePath.EndsWith("emotion_cgi_publish_v6"), "不得发出其它真实网络请求");
            PublishCount++; PublishForm = fields;
            return new HttpResponseMessage(mode == "publish-uncertain" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
                { Content = new StringContent(mode == "publish-uncertain" ? "{\"code\":-1}" : "{\"code\":0}") };
        }
    }

    private sealed class QzoneDraftLlm : ILlmClient
    {
        private readonly string reply;
        public QzoneDraftLlm(string reply) { this.reply = reply; }
        public string ProviderId => "qzone-check";
        public string Model => "fake";
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(new List<string> { "fake" });
        public Task<string> CompleteTextAsync(List<DeepSeekMessageData> messages, CancellationToken cancellationToken = default, string promptCacheKey = null)
            => Task.FromResult(reply);
        public Task<string> CompleteJsonAsync(List<DeepSeekMessageData> messages, CancellationToken cancellationToken = default, string promptCacheKey = null)
            => Task.FromResult(reply);
    }

    private sealed class QzoneCookieAdapter : ITracePlatformAdapter
    {
        public string PlatformId => "builtin.onebot";
        public PluginEventData ConvertInbound(string payload) => null;
        public Task<TraceCapabilityResultData> SendAsync(TraceOutboundMessageData message, TraceTurnContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("测试禁止向真人发消息");
        public Task<string> CallActionAsync(string action, Dictionary<string, object> parameters, CancellationToken cancellationToken = default)
            => Task.FromResult("{\"data\":{\"cookies\":\"uin=o123; skey=fake; p_skey=fake\"}}");
    }
}
