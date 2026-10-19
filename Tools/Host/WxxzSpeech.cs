using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TraceSoul2.Host;

// Synthesis only. Platform delivery and the main model remain outside this client.
public sealed class WxxzSpeech
{
    public sealed record WakeAudio(byte[] Audio, string Cache, long ElapsedMs, long? SynthesisMs, long? AudioMs);
    public bool SupportsWakeCache => settings.engine == "qwen_local";
    public sealed class Settings
    {
        public string engine { get; set; } = "system";
        public string localVoice { get; set; } = "character";
        public string remoteModel { get; set; } = "";
        public string remoteVoice { get; set; } = "";
    }
    private readonly string path, remoteConfigPath, localConfigPath;
    private readonly Func<HttpMessageHandler> handlerFactory;
    private readonly Func<LlmProviderRecord> localProvider;
    private Settings settings;
    public static WxxzSpeech ForProvider(Func<LlmProviderRecord> provider, Func<HttpMessageHandler> handlerFactory = null)
        => new WxxzSpeech(provider, handlerFactory);
    private WxxzSpeech(Func<LlmProviderRecord> provider, Func<HttpMessageHandler> handlerFactory)
    {
        localProvider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.handlerFactory = handlerFactory;
        path = remoteConfigPath = localConfigPath = "";
        settings = new Settings { engine = "qwen_local" };
    }
    public WxxzSpeech(string path, string remoteConfigPath, Func<HttpMessageHandler> handlerFactory = null, string localConfigPath = null,
        Func<LlmProviderRecord> localProvider = null)
    {
        this.path = path; this.remoteConfigPath = remoteConfigPath; this.handlerFactory = handlerFactory;
        this.localProvider = localProvider;
        this.localConfigPath = localConfigPath ?? Path.Combine(Path.GetDirectoryName(path), "local-voice-client.json");
        settings = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) : new Settings();
        Validate(settings);
    }
    private static void Validate(Settings value)
    {
        if (value == null || value.engine is not ("system" or "qwen_local" or "remote"))
            throw new ArgumentException("请选择系统、本地千问或远端语音。");
        if (string.IsNullOrWhiteSpace(value.localVoice) || value.localVoice.Length > 48 ||
            System.Text.RegularExpressions.Regex.IsMatch(value.localVoice, "[^a-zA-Z0-9_-]"))
            throw new ArgumentException("本地音色ID无效。");
        if (value.remoteModel == null || value.remoteVoice == null || value.remoteModel.Length > 160 || value.remoteVoice.Length > 160)
            throw new ArgumentException("远端模型或音色ID无效。");
    }
    public string Description => settings.engine switch {
        "qwen_local" => LocalDescription(),
        "remote" => "远端角色语音",
        _ => "Windows 系统测试音色"
    };
    private string LocalDescription()
    {
        if (localProvider == null) return "本地千问 · " + settings.localVoice;
        try { var provider = localProvider(); return provider.displayName + " · " + provider.voice; }
        catch (InvalidOperationException) { return "本地千问 · 请配置大脑语音槽"; }
    }
    public object Public()
    {
        string model = "", voice = "";
        var configured = false;
        try
        {
            using var doc = ReadRemote();
            model = Read(doc.RootElement, "model"); voice = Read(doc.RootElement, "voice_id");
            if (voice.Length == 0) voice = Read(doc.RootElement, "voice");
            configured = Read(doc.RootElement, "api_url").Length > 0 && Read(doc.RootElement, "api_key").Length > 0;
        }
        catch (InvalidOperationException) { }
        var effectiveVoice = settings.localVoice;
        if (localProvider != null) { try { effectiveVoice = localProvider().voice; } catch (InvalidOperationException) { } }
        return new { settings.engine, localVoice = effectiveVoice, localProviderManaged = localProvider != null, settings.remoteModel, settings.remoteVoice,
            remoteDefaultModel = model, remoteDefaultVoice = voice, remoteConfigured = configured,
            description = Description };
    }
    public void Save(Settings value)
    {
        Validate(value);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(value));
    }
    private JsonDocument ReadRemote()
    {
        if (string.IsNullOrWhiteSpace(remoteConfigPath))
            throw new InvalidOperationException("未配置远端语音。");
        try { return JsonDocument.Parse(File.ReadAllText(remoteConfigPath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { throw new InvalidOperationException("现有远端语音配置无法读取；请先配置语音接口。 "); }
    }
    private static string Read(JsonElement root, string key) => root.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
    private (Uri endpoint, string key, string model, string voice) LocalConnection()
    {
        if (localProvider != null)
        {
            var provider = localProvider();
            return (new Uri(QwenTtsProvider.SpeechUrl(provider)), provider.apiKey, provider.model, provider.voice);
        }
        try
        {
            using var doc=JsonDocument.Parse(File.ReadAllText(localConfigPath));
            var key=Read(doc.RootElement,"api_key");
            var url=Read(doc.RootElement,"api_url");
            if (!System.Text.RegularExpressions.Regex.IsMatch(key,"^[A-Za-z0-9_-]{32,256}$") ||
                !Uri.TryCreate(url,UriKind.Absolute,out var endpoint) || endpoint.UserInfo.Length>0 ||
                endpoint.Query.Length>0 || endpoint.Fragment.Length>0 || !endpoint.AbsolutePath.EndsWith("/v1/audio/speech",StringComparison.Ordinal) ||
                !(endpoint.Scheme=="https" || endpoint.Scheme=="http" && endpoint.IsLoopback))
                throw new InvalidOperationException();
            return (endpoint,key,"qwen3-tts-local",settings.localVoice);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { throw new InvalidOperationException("本地千问缺少有效的私有接口配置或密钥；请检查local-voice配置。"); }
    }
    public async Task<string> WorkbenchAsync(CancellationToken token)
    {
        var connection=LocalConnection();
        var baseUrl=connection.endpoint.AbsoluteUri[..^"v1/audio/speech".Length];
        using var handler=handlerFactory?.Invoke() ?? new HttpClientHandler {AllowAutoRedirect=false,UseProxy=false};
        using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(10),MaxResponseContentBufferSize=4096};
        using var request=new HttpRequestMessage(HttpMethod.Post,baseUrl+"auth/ticket");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",connection.key);
        try
        {
            using var response=await client.SendAsync(request,token);
            if(!response.IsSuccessStatusCode) throw new InvalidOperationException("本地语音验证失败，请检查服务和密钥。");
            using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var ticket=Read(doc.RootElement,"ticket");
            if(!System.Text.RegularExpressions.Regex.IsMatch(ticket,"^[A-Za-z0-9_-]{43}$"))
                throw new InvalidOperationException("本地语音登录回执无效。");
            return baseUrl+"auth/launch?ticket="+ticket;
        }
        catch(HttpRequestException){throw new InvalidOperationException("本地语音服务未连接，请先启动本地TTS。");}
        catch(JsonException){throw new InvalidOperationException("本地语音登录回执无效。");}
        catch(OperationCanceledException) when(!token.IsCancellationRequested)
        {throw new InvalidOperationException("本地语音验证超时，请稍后从入口重新打开。");}
    }
    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken token)
        => (await SynthesizeResultAsync(text, token, false)).Audio;
    public Task<WakeAudio> SynthesizeWakeAsync(string text, CancellationToken token)
        => SynthesizeResultAsync(text, token, true);
    private async Task<WakeAudio> SynthesizeResultAsync(string text, CancellationToken token, bool wake)
    {
        try { return await SynthesizeCoreAsync(text, token, wake); }
        catch (HttpRequestException) { throw new InvalidOperationException("语音接口连接失败；未重试或切换音色。"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("语音合成超时；未重试或播放。"); }
    }
    private async Task<WakeAudio> SynthesizeCoreAsync(string text, CancellationToken token, bool wake)
    {
        if (settings.engine == "system") return new(null, "unsupported", 0, null, null);
        if (wake && (text?.Length ?? 0) > 30) throw new InvalidOperationException("唤醒应答不能超过30字。");
        if (string.IsNullOrWhiteSpace(text) || text.Length > (settings.engine == "qwen_local" ? 240 : 500))
            throw new InvalidOperationException("语音文字为空或超出所选服务的单次限制；未截断或播放。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        token = deadline.Token; // Also bounds body reads after ResponseHeadersRead completes.
        string url, model, voice, key = "", proxy = "";
        if (settings.engine == "qwen_local")
        {
            var connection=LocalConnection();
            url = connection.endpoint.AbsoluteUri; key=connection.key;
            model = connection.model; voice = connection.voice;
        }
        else
        {
            using var doc = ReadRemote();
            var root = doc.RootElement;
            // Endpoint override and its credential are one pair. Never borrow a
            // different provider's key when the hosts differ.
            url = Read(root, "api_url"); key = Read(root, "api_key"); proxy = Read(root, "proxy");
            model = settings.remoteModel.Length > 0 ? settings.remoteModel : Read(root, "model");
            voice = settings.remoteVoice.Length > 0 ? settings.remoteVoice : Read(root, "voice_id");
            if (voice.Length == 0) voice = Read(root, "voice");
            if (key.Length == 0 || model.Length == 0 || voice.Length == 0)
                throw new InvalidOperationException("远端语音缺少接口凭据、模型或音色；未回退到其他声音。");
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.UserInfo.Length > 0 ||
            !(endpoint.Scheme == "https" || endpoint.Scheme == "http" && endpoint.IsLoopback))
            throw new InvalidOperationException("语音接口应使用HTTPS或本机回环HTTP地址。");
        using var handler = handlerFactory?.Invoke() ?? new HttpClientHandler {
            AllowAutoRedirect = false, UseProxy = proxy.Length > 0,
            Proxy = proxy.Length > 0 ? new WebProxy(proxy) : null };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        var cacheRequested = wake && SupportsWakeCache;
        if (cacheRequested) request.Headers.Add("X-TraceSoul-Wake-Cache", "v1");
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(JsonSerializer.Serialize(new {
            model, input = text, voice, response_format = "wav" }), Encoding.UTF8, "application/json");
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("语音合成返回HTTP " + (int)response.StatusCode + "；未重试或切换音色。");
        const int limit = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new InvalidOperationException("语音音频超过4MB；未播放。");
        using var audio = new MemoryStream();
        using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (audio.Length + count > limit) throw new InvalidOperationException("语音音频超过4MB；未播放。");
            audio.Write(buffer, 0, count);
        }
        var bytes = audio.ToArray();
        if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidOperationException("语音接口未返回WAV；未播放或回退。");
        string cache = "unsupported";
        if (cacheRequested && response.Headers.TryGetValues("X-TraceSoul-Wake-Cache", out var values))
        {
            var value = string.Join("", values);
            if (value is "hit" or "miss") cache = value;
        }
        long? Metric(string name) => response.Headers.TryGetValues(name, out var fields) &&
            long.TryParse(string.Join("", fields), out var number) && number >= 0 ? number : null;
        return new(bytes, cache, started.ElapsedMilliseconds, Metric("X-Synthesis-Ms"), Metric("X-Audio-Ms"));
    }
}
