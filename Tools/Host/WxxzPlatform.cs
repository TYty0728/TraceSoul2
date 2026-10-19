using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Plugins;
using TraceSoul2.Util;

namespace TraceSoul2.Host
{
    /// <summary>One local USB body. Conversation mutations use the Soul gate; bounded audio waits and cache preparation run independently.</summary>
    public sealed class WxxzPlatform : ITracePlugin, IDisposable
    {
        public const string Id = "wxxz";
        public const string HeardPrefix = "【我听见】";
        private readonly HttpClient http;
        private readonly string bridgeKey;
        private readonly SemaphoreSlim bodyAudio = new(1,1);
        private TracePluginServices services;
        private string session;
        private bool exclusive;
        private bool companionDisplay;
        private readonly HashSet<string> requests = new(StringComparer.Ordinal);
        private object delivery;
        private string captureId, capturedText;
        private string capturedRawText;
        private string capturedCallId;
        private double? capturedVoiceScore;
        private readonly WxxzVoiceprint voiceprint;
        private readonly WxxzSpeech speech;
        private readonly WxxzAttention attention;
        private readonly object wakeAudioSync = new();
        private readonly SemaphoreSlim wakePreparation = new(1,1);
        private object lastWakePreparation, lastWakeUse;
        private string VoiceDescription => speech?.Description ?? "Windows 系统测试音色";
        private readonly object callSync = new();
        private sealed class VoiceCall { public string Id, Session, ListenId; public bool WakeMode, Background; }
        private VoiceCall currentCall;
        public readonly WxxzDiagnostics Diagnostics = new();
        private readonly WxxzDisplay displayState = new();
        private WxxzDisplay.Ticket displayTurn;
        public Func<WxxzVoiceMonitor.Snapshot> MonitorStatus { get; set; }
        public Func<string,bool> ManagedAdmissionAllowed {get;set;}
        private bool BackgroundBusy => MonitorStatus?.Invoke().active == true;
        private string activeCheckId;
        public string SessionId => session;
        public string ContextConversationId { get; private set; }
        public TracePluginMetadataData Metadata { get; } = new()
        {
            Id = Id, DisplayName = "WXXZ（微雪小智）", Version = "0.1.0",
            Author = "TraceSoul2", Role = PluginRoleValues.Platform, PlatformId = Id,
            Description = "本机 USB 开发板：麦克风听写或手动明确发言，回复沿原会话显示并按当前语音配置播放。"
        };

        public WxxzPlatform(HttpMessageHandler handler = null, string voiceprintPath = null, WxxzSpeech speech = null,
            Func<DateTimeOffset> clock = null,string bridgeUrl = null,string bridgeKey = null)
        {
            var address=BridgeAddress(bridgeUrl??Environment.GetEnvironmentVariable("TRACESOUL2_WXXZ_BRIDGE_URL"),
                bridgeKey??Environment.GetEnvironmentVariable("TRACESOUL2_WXXZ_BRIDGE_KEY"));
            this.bridgeKey=address.key;
            this.speech = speech;
            voiceprint = new WxxzVoiceprint(voiceprintPath);
            attention = new WxxzAttention(voiceprintPath == null ? null : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(voiceprintPath), "wake.json"),
                () => services?.Storage.LoadPairIdentity().Assname ?? "", clock);
            http = new HttpClient(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { BaseAddress = address.url, Timeout = TimeSpan.FromMinutes(5) };
        }
        internal static (Uri url,string key) BridgeAddress(string url,string key)
        {
            url=string.IsNullOrWhiteSpace(url)?"http://127.0.0.1:5089/":url.Trim();
            key=key?.Trim()??"";
            if(!Uri.TryCreate(url,UriKind.Absolute,out var endpoint)||endpoint.UserInfo.Length>0||endpoint.Query.Length>0||
                endpoint.Fragment.Length>0||!(endpoint.Scheme=="https"||endpoint.Scheme=="http"&&endpoint.IsLoopback)||
                key.Length>0&&!System.Text.RegularExpressions.Regex.IsMatch(key,"^[A-Za-z0-9_-]{32,256}$")||
                !endpoint.IsLoopback&&key.Length==0)
                throw new InvalidOperationException("WXXZ桥地址只支持回环HTTP或带独立密钥的HTTPS，不含凭据、查询参数或片段；请检查环境配置。");
            return(new Uri(endpoint.AbsoluteUri.TrimEnd('/')+"/"),key);
        }
        public void Register(TracePluginContext context)
        {
            services = context.Services;
            context.Services.Platforms.Register(new PlatformHandle
            { Id = Id, DisplayName = "WXXZ（本机 USB）", IsConnected = () => session != null });
            context.AddCallable(new Speaker(this));
        }
        public void Shutdown()
        {
            string pending;
            lock(callSync) {pending=currentCall?.ListenId;currentCall=null;}
            attention.Stop();session=null;captureId=capturedText=capturedRawText=capturedCallId=null;
            displayState.Reset(null);
            voiceprint.ClearPending();requests.Clear();
            if(pending!=null) _=CancelShutdownCaptureAsync(pending);
        }
        private async Task CancelShutdownCaptureAsync(string listenId)
        {
            try
            {
                using var bounded=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await Request("api/listen/cancel",new {listen_id=listenId},bounded.Token);
            }
            catch {Diagnostics.Add("monitor","stop_unconfirmed","设备关闭时取消采音的回执未知。");}
        }
        public void Dispose() { Shutdown(); http.Dispose(); }
        public object Status() => new { sessionId = session, deviceId = "local-usb",
            privateSession = exclusive, delivery, voice = VoiceDescription, speech = speech?.Public(), input = "板载麦克风听写 / 管理员手动文字",
            voiceprint = voiceprint.Status(), callId = CallId, wake = attention.Configuration(), attention = attention.State(),
            monitor = MonitorStatus?.Invoke(), diagnostics = Diagnostics.Snapshot(), wakeAudio = WakeAudioStatus(), companionDisplay,
            displayState = DisplayState() };
        public WxxzDisplay.Snapshot DisplayState()
        {
            bool wake;lock(callSync)wake=currentCall?.WakeMode==true;
            return displayState.Read(attention.Configuration().name,attention.State(),MonitorStatus?.Invoke(),wake);
        }
        private object WakeAudioStatus()
        {
            lock(wakeAudioSync) return new { supported = speech?.SupportsWakeCache == true,
                lastPreparation = lastWakePreparation, lastUse = lastWakeUse };
        }
        private object WakeAudioReceipt(string text, WxxzSpeech.WakeAudio audio) => new {
            at = DateTimeOffset.UtcNow, text, cache = audio.Cache, elapsedMs = audio.ElapsedMs,
            synthesisMs = audio.SynthesisMs, audioMs = audio.AudioMs, bytes = audio.Audio?.Length ?? 0 };
        public async Task<object> PrepareWakeAudioAsync(CancellationToken token, bool monitorStarting = false)
        {
            if(CallId != null || BackgroundBusy && !monitorStarting) throw new InvalidOperationException("请先暂停监听/结束通话，再预生成唤醒应答。");
            if(speech?.SupportsWakeCache != true) throw new InvalidOperationException("预生成唤醒语音仅支持本地千问；请先保存播放音色。");
            if(!await wakePreparation.WaitAsync(0,token)) throw new InvalidOperationException("唤醒语音正在预生成，请等待完成。");
            try
            {
                var text = attention.Configuration().reply;
                var result = await speech.SynthesizeWakeAsync(text,token);
                var receipt = WakeAudioReceipt(text,result);
                lock(wakeAudioSync) lastWakePreparation = receipt;
                return receipt; // Synthesis only: no device request, Moment or main-model call.
            }
            finally {wakePreparation.Release();}
        }
        public bool ShouldPrepareWakeAudio => attention.Configuration().enabled && speech?.SupportsWakeCache == true;
        public object ConfigureWake(WxxzAttention.Settings value)
        {
            if (CallId != null || BackgroundBusy) throw new InvalidOperationException("请先暂停名字监听/结束通话，再修改称呼。");
            attention.Save(value); return Status();
        }
        public object PreviewWords(string text) => attention.Correct(text);
        public object PreviewAttention(string text, bool focused, string speaker) => attention.Preview(text,focused,speaker);
        public (string id, string display, string audio) DeliveryState()
        {
            if(delivery==null) return (null,null,null);
            var value=JsonSerializer.SerializeToElement(delivery);
            return (value.GetProperty("outputId").GetString(),value.GetProperty("display").GetString(),value.GetProperty("audio").GetString());
        }
        public void CompleteResponse(bool success=true)
        {
            displayState.CompleteTurn(displayTurn,success);displayTurn=null;
            if (currentCall?.WakeMode == true) attention.Complete();
        }
        private string CallId { get { lock (callSync) return currentCall?.Id; } }

        public object ConfigureSpeech(WxxzSpeech.Settings settings)
        {
            if (CallId != null || BackgroundBusy) throw new InvalidOperationException("请先结束通话后切换音色。");
            if (speech == null) throw new InvalidOperationException("语音配置未初始化。");
            speech.Save(settings);
            return Status();
        }
        public Task<string> OpenVoiceWorkbenchAsync(CancellationToken token) => speech != null
            ? speech.WorkbenchAsync(token) : throw new InvalidOperationException("语音配置未初始化。");
        private async Task<JsonElement> SpeakAsync(string text, CancellationToken token, bool wake = false, object display = null,
            WxxzDisplay.Ticket displayTicket = null)
        {
            var acquired=false;
            try
            {
            await bodyAudio.WaitAsync(token);acquired=true;
            byte[] wav;
            if(wake && speech != null)
            {
                var result = await speech.SynthesizeWakeAsync(text,token);
                wav = result.Audio;
                lock(wakeAudioSync) lastWakeUse = WakeAudioReceipt(text,result);
                Diagnostics.Add("wake_audio",result.Cache,"唤醒应答取音完成，耗时"+result.ElapsedMs+"毫秒。");
            }
            else wav = speech == null ? null : await speech.SynthesizeAsync(text, token);
            var payload = new Dictionary<string,object>();
            if(wav==null)payload["text"]=text;
            else {payload["data"]=Convert.ToBase64String(wav);payload["engine"]=VoiceDescription;}
            if(display!=null)payload["display"]=display;
            displayState.Playing(displayTicket);
            var receipt=await Request("api/speak",payload,token);
            if(display!=null && (!receipt.TryGetProperty("display_complete",out var complete)||complete.ValueKind!=JsonValueKind.True||
                !receipt.TryGetProperty("display_pages",out var pages)||pages.GetInt32()<1||
                !receipt.TryGetProperty("display_receipts",out var frames)||frames.ValueKind!=JsonValueKind.Array||frames.GetArrayLength()!=pages.GetInt32()||
                frames.EnumerateArray().Any(r=>!r.TryGetProperty("status",out var state)||state.GetString()!="done"||
                    !r.TryGetProperty("protocol",out var protocol)||protocol.GetInt32()!=1)))
                throw new InvalidOperationException("WXXZ 未确认全部字幕页显示；未重发。");
            displayState.Finished(displayTicket,true);
            return receipt;
            }
            catch {displayState.Finished(displayTicket,false);throw;}
            finally {if(acquired)bodyAudio.Release();}
        }
        public async Task<object> TestSpeechAsync(string expectedSession, CancellationToken token)
        {
            if (session == null || expectedSession != session || CallId != null || BackgroundBusy)
                throw new InvalidOperationException("请先连接WXXZ，并结束通话后试听。");
            token.ThrowIfCancellationRequested();
            // Keep collecting bounded device receipts after physical playback begins.
            const string text="你好，我是小汐。我们现在测试角色语音。";
            var result = await SpeakAsync(text,CancellationToken.None,displayTicket:displayState.Prepare(session,CallId,text));
            return new { voice = VoiceDescription, details = result };
        }

        private async Task<JsonElement> Request(string path, object body, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            if(bridgeKey.Length>0)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",bridgeKey);
            using var response = await http.SendAsync(request, token);
            // Do not surface upstream bodies or media in errors/logs.
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("WXXZ USB 桥返回 HTTP " + (int)response.StatusCode + "；未重发。请检查 5089 控制页。");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!json.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("WXXZ USB 桥未确认成功；未重发。");
            if (path is "api/listen/cancel" or "api/capture/state") return json.RootElement.Clone();
            bool Valid(JsonElement receipt, string status) => receipt.ValueKind == JsonValueKind.Object &&
                receipt.TryGetProperty("protocol", out var protocol) && protocol.GetInt32() == 1 &&
                receipt.TryGetProperty("status", out var state) && state.GetString() == status;
            if (path == "api/speak")
            {
                if (!json.RootElement.TryGetProperty("receipts", out var receipts) || receipts.ValueKind != JsonValueKind.Array ||
                    receipts.GetArrayLength() == 0 || receipts.EnumerateArray().Any(x => !Valid(x, "done")))
                    throw new InvalidOperationException("WXXZ 未收到完整播放完成回执；未重发。");
            }
            else if (!json.RootElement.TryGetProperty("receipt", out var receipt) || !Valid(receipt, path == "api/status" ? "ready" : "done"))
                throw new InvalidOperationException("WXXZ 未收到有效板端回执；未重发。");
            return json.RootElement.Clone();
        }

        public async Task<object> ListenAsync(string expectedSession, CancellationToken token)
        {
            if (CallId != null || BackgroundBusy) throw new InvalidOperationException("请先结束语音通话。");
            if (session == null || expectedSession != session)
                throw new InvalidOperationException("WXXZ 会话已失效，请重新连接。");
            // Finite, explicit capture; final text must still be confirmed/submitted by the user.
            var result = await Request("api/listen", new { duration_ms = 8000 }, CancellationToken.None);
            capturedCallId = null;
            capturedRawText = result.GetProperty("text").GetString()?.Trim() ?? "";
            capturedText = attention.Correct(capturedRawText).Text;
            if (capturedText.Length > 4000) throw new InvalidOperationException("听写结果超长；未提交。");
            captureId = capturedText.Length > 0 ? Guid.NewGuid().ToString("N") : null;
            return new { text = capturedText, rawText = capturedRawText, corrected = capturedRawText != capturedText, captureId, details = result };
        }

        public async Task<object> EnrollAsync(string expectedSession, bool restart, CancellationToken token, int? sampleNumber = null, string requestId = null)
        {
            if (session == null || expectedSession != session || CallId != null || BackgroundBusy)
                throw new InvalidOperationException("请先连接WXXZ，并结束通话后录入。");
            if (sampleNumber is < 1 or > 3) throw new ArgumentException("请选择第1～3段声纹。");
            if (requestId != null && !Guid.TryParseExact(requestId, "N", out _)) throw new ArgumentException("无效的录音编号。");
            token.ThrowIfCancellationRequested();
            var id = requestId ?? Guid.NewGuid().ToString("N");
            lock (callSync) activeCheckId = id;
            try
            {
                var result = await Request("api/voice/sample", new { listen_id = id }, CancellationToken.None);
                var quality = result.TryGetProperty("quality", out var diagnostics) ? (object)diagnostics : null;
                var text = diagnostics.ValueKind == JsonValueKind.Object && diagnostics.TryGetProperty("full_text", out var transcript)
                    ? transcript.GetString() ?? "" : "";
                try
                {
                    return new { accepted = true, voiceprint = voiceprint.Add(result.GetProperty("voice"), restart, sampleNumber, text),
                        receipt = result.GetProperty("receipt"), quality };
                }
                catch (InvalidOperationException error)
                {
                    return new { accepted = false, reason = error.Message, voiceprint = voiceprint.Status(),
                        receipt = result.GetProperty("receipt"), quality };
                }
            }
            finally { lock (callSync) activeCheckId = null; }
        }

        public async Task<object> CheckVoiceAsync(string expectedSession, string requestId, CancellationToken token)
        {
            if (session == null || expectedSession != session || CallId != null || BackgroundBusy)
                throw new InvalidOperationException("请先连接WXXZ，并结束通话后诊断。");
            if (!voiceprint.IsEnrolled) throw new InvalidOperationException("请先录入本人声纹。");
            if (!Guid.TryParseExact(requestId, "N", out var checkId)) throw new ArgumentException("无效的声纹诊断编号。");
            token.ThrowIfCancellationRequested();
            lock (callSync) activeCheckId = checkId.ToString("N");
            try
            {
                var result = await Request("api/voice/sample", new { listen_id = checkId.ToString("N") }, CancellationToken.None);
                return new { diagnostic = voiceprint.Inspect(result.GetProperty("voice")),
                    quality = result.TryGetProperty("quality", out var quality) ? (object)quality : null,
                    receipt = result.GetProperty("receipt") };
            }
            finally { lock (callSync) activeCheckId = null; }
        }

        public async Task<object> CheckVoiceStateAsync(string expectedSession, string requestId, CancellationToken token)
        {
            lock (callSync)
                if (session == null || expectedSession != session || activeCheckId != requestId)
                    return new { phase = "inactive", recorded_ms = 0 };
            return await Request("api/capture/state", new { listen_id = requestId }, token);
        }

        public void ValidateCallStart(string expectedSession, bool wakeMode = false, bool background = false)
        {
            if (!background && BackgroundBusy) throw new InvalidOperationException("后台监听尚未结束，请勿重复采音。");
            if (session == null || expectedSession != session) throw new InvalidOperationException("请先连接 WXXZ 会话。");
            if (!voiceprint.IsEnrolled) throw new InvalidOperationException("请先录入本人声纹，再开始自动语音通话。");
            if (wakeMode && attention.Configuration().enabled && string.IsNullOrWhiteSpace(attention.Configuration().name))
                throw new InvalidOperationException("请先在身体与感官设置唤醒名字。");
            lock(callSync) if(currentCall != null) throw new InvalidOperationException("已有通话，请先结束。");
        }
        public async Task<object> StartCallAsync(string expectedSession, CancellationToken token, bool wakeMode = false, bool background = false)
        {
            ValidateCallStart(expectedSession,wakeMode,background);
            var status = await Request("api/status", new { }, token);
            if (!status.GetProperty("receipt").TryGetProperty("firmware", out var version) || version.GetString() != "0.3.0")
                throw new InvalidOperationException("实时通话需要0.3.0感官固件，请先更新开发板。");
            token.ThrowIfCancellationRequested();
            lock (callSync)
            {
                if (currentCall != null) throw new InvalidOperationException("已有通话，请先结束。");
                currentCall = new VoiceCall { Id = Guid.NewGuid().ToString("N"), Session = session, WakeMode = wakeMode && attention.Configuration().enabled, Background=background };
            }
            if (currentCall.WakeMode) attention.Start(); else attention.Stop();
            displayState.StartCall(session,CallId);
            captureId = capturedText = capturedRawText = capturedCallId = null;
            return Status();
        }

        public async Task<object> StopCallAsync(string expectedCall, CancellationToken token)
        {
            string listenId;
            lock (callSync)
            {
                if (currentCall == null || currentCall.Id != expectedCall) return new { stopped = true };
                listenId = currentCall.ListenId;
                currentCall = null; // Reject a late transcript immediately, without waiting for the Soul gate.
                attention.Stop();
                displayState.StopCall(expectedCall);
            }
            if (listenId != null)
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
                bounded.CancelAfter(TimeSpan.FromSeconds(3));
                await Request("api/listen/cancel", new { listen_id = listenId }, bounded.Token);
            }
            return new { stopped = true };
        }

        public sealed record CallCapture(string Session, string Call, DateTimeOffset Started, JsonElement Result);
        public async Task<CallCapture> CaptureCallAsync(string expectedSession, string expectedCall, CancellationToken token, bool background=false)
        {
            var listenId = Guid.NewGuid().ToString("N");
            lock (callSync)
            {
                if (currentCall == null || currentCall.Id != expectedCall || currentCall.Session != expectedSession)
                    throw new InvalidOperationException("语音通话已结束。");
                if(currentCall.Background!=background) throw new InvalidOperationException("此通话由后台管理，网页不能重复驱动采音。");
                if (currentCall.ListenId != null) throw new InvalidOperationException("本次采音尚未完成，请勿重复监听。");
                currentCall.ListenId = listenId;
            }
            try
            {
                token.ThrowIfCancellationRequested();
                await bodyAudio.WaitAsync(token);
                try
                {
                lock(callSync)
                    if(currentCall?.Id!=expectedCall) return new CallCapture(expectedSession,expectedCall,attention.Now,default);
                var started = attention.Now;
                displayState.Capture(expectedSession,expectedCall,listenId,true);
                var result = await Request("api/utterance", new { listen_id = listenId }, token);
                return new CallCapture(expectedSession,expectedCall,started,result);
                }
                finally {bodyAudio.Release();}
            }
            finally
            {
                displayState.Capture(expectedSession,expectedCall,listenId,false);
                lock(callSync) if(currentCall?.Id==expectedCall) currentCall.ListenId=null;
            }
        }
        public async Task<object> ListenCallAsync(string expectedSession,string expectedCall,CancellationToken token) =>
            await CompleteCallAsync(await CaptureCallAsync(expectedSession,expectedCall,CancellationToken.None),CancellationToken.None);
        public async Task<object> CompleteCallAsync(CallCapture capture,CancellationToken token)
        {
                var expectedCall=capture.Call;
                bool wakeMode;
                lock(callSync)
                {
                    if(currentCall?.Id!=expectedCall || currentCall.Session!=capture.Session)
                    {
                        Diagnostics.Add("decision","cancelled","采音返回时通话已结束，迟到结果作废。");
                        return new {status="cancelled",text=""};
                    }
                    wakeMode=currentCall.WakeMode;
                }
                captureId = capturedText = capturedRawText = capturedCallId = null;
                var result=capture.Result;
                var text = result.GetProperty("text").GetString()?.Trim() ?? "";
                var state = result.GetProperty("status").GetString();
                var matched = voiceprint.Match(result.GetProperty("voice"));
                if (text.Length == 0)
                {
                    Diagnostics.Add("decision",state,"无可用听写，本次不进入Moment。",score:matched.score,voiceConfirmed:matched.matched);
                    return new { status = state, text = "", score = matched.score, attention = attention.State() };
                }
                if (text.Length > 4000) throw new InvalidOperationException("听写结果超长；未提交。");
                var corrected = attention.Correct(text);
                var speechAt = result.TryGetProperty("started_ms", out var onset) && onset.ValueKind==JsonValueKind.Number && onset.TryGetInt32(out var milliseconds) && milliseconds >= 0 && milliseconds <= 25000
                    ? capture.Started.AddMilliseconds(milliseconds) : attention.Now;
                var decision=WxxzAttention.Decide(corrected,wakeMode,attention.FocusedAt(speechAt),matched.matched);
                Diagnostics.Add("decision",decision.outcome,decision.reason,corrected,matched.score,matched.matched);
                if (decision.acknowledge)
                {
                    attention.Busy();
                    await AcknowledgeWakeAsync(expectedCall, matched.matched,token);
                    lock(callSync)
                        if (currentCall?.Id != expectedCall) return new { status = "cancelled", text = "" };
                    attention.Complete();
                    if (!decision.admit)
                        return new { status = "wake_ack", text = "", voiceConfirmed = matched.matched, attention = attention.State() };
                }
                if (!decision.admit) return new { status = decision.outcome, text = "", score = matched.score, attention = attention.State() };
                capturedRawText = text; capturedText = corrected.Text; captureId = Guid.NewGuid().ToString("N");
                capturedCallId = expectedCall; capturedVoiceScore = matched.score;
                if (wakeMode) attention.Complete();
                return new { status = "verified", text = capturedText, rawText = corrected.Changed ? text : null, corrected = corrected.Changed,
                    captureId, score = matched.score, receipt = result.GetProperty("receipt"), attention = attention.State() };
        }

        private async Task AcknowledgeWakeAsync(string expectedCall, bool voiceConfirmed,CancellationToken token)
        {
            lock(callSync) if(currentCall?.Id != expectedCall) return;
            var config = attention.Configuration();
            var outputId = Guid.NewGuid().ToString("N");
            var displayTicket=displayState.Prepare(session,expectedCall,config.reply);
            delivery = new { outputId, display = "pending", audio = "pending", wake = true };
            try
            {
                await Request("api/text", new { text = config.reply }, token);
                lock(callSync) if(currentCall?.Id != expectedCall)
                { delivery = new { outputId, display = "completed", audio = "not_started", wake = true }; return; }
                await SpeakAsync(config.reply, token, wake:true,displayTicket:displayTicket);
                delivery = new { outputId, display = "completed", audio = "completed", wake = true };
                services.Storage.SaveOperationalEvent(new OperationalEventRecord
                {
                    Id = outputId, ConversationId = ContextConversationId ?? "wxxz:session:" + session,
                    Kind = OperationalEventKindValues.PluginRuntime, SourcePluginId = Id, SourceEventId = outputId,
                    Role = "system_event", Content = "WXXZ名字唤醒，简短应答已播放。",
                    PayloadJson = TraceJson.ToJson(new { wake_name = config.name, voice_verified = voiceConfirmed, reply = config.reply }),
                    CreatedUnixMs = attention.Now.ToUnixTimeMilliseconds(), OccurredUnixMs = attention.Now.ToUnixTimeMilliseconds()
                });
            }
            catch { displayState.Finished(displayTicket,false);attention.Stop(); delivery = new { outputId, display = "failed_or_unknown", audio = "failed_or_unknown", wake = true }; throw; }
        }

        public async Task<object> StartAsync(bool privateSession, CancellationToken token)
        {
            if (CallId != null || BackgroundBusy) throw new InvalidOperationException("请先结束语音通话。");
            if (services == null) throw new InvalidOperationException("WXXZ 插件未启用。");
            var status=await Request("api/status", new { }, token);
            companionDisplay=status.TryGetProperty("display_version",out var version)&&version.GetInt32()==1;
            var settings = EnvironmentLogic.Settings(services.Storage);
            if (!settings.OwnerAccounts.Any(x => x.PlatformId == Id && x.UserId == "local-owner"))
            {
                settings.OwnerAccounts.Add(new OwnerAccountBindingData { PlatformId = Id, UserId = "local-owner" });
                EnvironmentLogic.SaveSettings(services.Storage, settings);
            }
            session = Guid.NewGuid().ToString("N");
            displayState.Reset(session);
            exclusive = privateSession;
            requests.Clear();
            captureId = capturedText = capturedRawText = capturedCallId = null;
            delivery = null;
            ContextConversationId = null;
            return Status();
        }
        public PluginEventData Receive(string expectedSession, string requestId, string text, string role, string root, string fromCaptureId = null, string backgroundCall = null)
        {
            lock(callSync)
            {
            if(backgroundCall!=null && (currentCall?.Id!=backgroundCall || ManagedAdmissionAllowed?.Invoke(backgroundCall)==false))
                throw new OperationCanceledException("后台通话已停止，本次输入不准入。");
            if(currentCall?.Background==true && backgroundCall!=currentCall.Id)
                throw new InvalidOperationException("后台通话已停止或由其他流程管理，本次输入不准入。");
            if (session == null || expectedSession != session) throw new InvalidOperationException("WXXZ 会话已关闭或过期，请重新连接。");
            if (!Guid.TryParse(requestId, out var eventId)) throw new ArgumentException("请提供有效的请求编号。");
            text = (text ?? "").Trim();
            if (text.Length == 0 || text.Length > 4000) throw new ArgumentException("明确发言应为 1～4000 字。");
            var spoken = !string.IsNullOrEmpty(fromCaptureId);
            if (spoken && (captureId != fromCaptureId || capturedText != text))
                throw new InvalidOperationException("录音结果已失效或文字已修改，请作为手动文字确认发送。");
            if (spoken && capturedCallId != null && CallId != capturedCallId)
                throw new InvalidOperationException("语音通话已结束，迟到的听写不会提交。");
            if (requests.Count >= 256) throw new InvalidOperationException("此会话已满，请重新连接。");
            if (!requests.Add(eventId.ToString("N"))) throw new InvalidOperationException("该发言已提交，结果可能未知；不会重复执行。");
            if (spoken && currentCall?.WakeMode == true) attention.Busy();
            if(spoken) Diagnostics.Add("admission","admitted","真实板载听写已核验并交给主逻辑；等待处理结果。",
                attention.Correct(capturedRawText),capturedVoiceScore,capturedCallId!=null,true);
            var source = new PluginEventData
            {
                PluginId = Id, ExternalEventId = session + ":" + eventId.ToString("N"),
                Role = role, Content = text, Type = spoken ? MomentTypes.Hearing : MomentTypes.Message,
                SourcePlatform = "wxxz", Realm = TraceRealmValues.SharedScene,
                EvidenceType = EvidenceTypeValues.DialogueExplicit, Organ = spoken ? BodyOrganValues.Voice : BodyOrganValues.Text,
                Breaking = true, Wake = KernelWakeValues.Dialogue,
                Environment = new EnvironmentObservationData { PlatformId = Id, AccountId = "local-usb",
                    SessionId = session, SessionType = exclusive ? "private" : "group", SpeakerId = "local-owner", SpeakerName = role,
                    ExclusivePair = exclusive, DirectAddress = true, ParticipantIds = new[] { "local-owner" } },
                PayloadJson = TraceJson.ToJson(new { platform = "WXXZ", kind = "direct_dialogue", input = spoken ? "board_final_transcript" : "admin_simulation", capture_id = spoken ? captureId : null, admission = "direct",
                    voice_verified = spoken && capturedCallId != null, voice_score = spoken && capturedCallId != null ? capturedVoiceScore : null,
                    raw_transcript = spoken ? capturedRawText : null, name_corrected = spoken && capturedRawText != text }),
                OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            if (spoken) captureId = capturedText = capturedRawText = capturedCallId = null;
            ContextConversationId = EnvironmentLogic.Resolve(services.Storage, root, source).ContextConversationId;
            displayTurn=displayState.BeginTurn(session);
            return source;
            }
        }
        private bool Matches(TraceTurnContext turn) => session != null && turn?.Environment?.PlatformId == Id &&
            turn.Environment.AccountId == "local-usb" && turn.Environment.SessionId == session;

        private sealed class Speaker : ITraceCallableContribution
        {
            private readonly WxxzPlatform owner;
            public Speaker(WxxzPlatform owner) { this.owner = owner; }
            public TraceContributionDescriptorData Descriptor { get; } = new()
            {
                Id = "wxxz.send", Kind = TraceContributionKindValues.Effector, DisplayName = "开发板文字与语音",
                Description = "把本轮回复显示到原 WXXZ 开发板，并按当前语音配置播放。支持板载麦克风听写；相机尚未接入。",
                Provides = "expression.text.send", BodyId = Id, BodyTier = BodyTierValues.Chat,
                Organ = BodyOrganValues.Text, SupportsPublicEnvironment = true,
                ParametersJsonSchema = "{text:string}", HasExternalSideEffect = true
            };
            public bool IsAvailable(TraceTurnContext context) => owner.Matches(context);
            public async Task<TraceCapabilityResultData> ExecuteAsync(BrainCapabilityCallData call,
                TraceTurnContext context, CancellationToken token)
            {
                if (!owner.Matches(context)) throw new InvalidOperationException("WXXZ 原会话已失效；未转发其他平台。");
                var text = call.GetArgument("text").Trim();
                if (text.Length == 0 || text.Length > 500) throw new InvalidOperationException("WXXZ 回复须为 1～500 字；本次未发送。");
                var outputId = Guid.NewGuid().ToString("N");
                var expression=context.Workspace.ExpressionMatch;
                // Keep the input's call identity: an already admitted reply may finish after stop,
                // but must not paint a newer call (or look like a new manual output).
                var turn=owner.displayTurn;
                var displayTicket=owner.displayState.Prepare(turn?.Session??owner.session,turn?.Call,text,
                    expression?.status=="matched"?expression.selectedId:null);
                owner.delivery = new { outputId, display = "pending", audio = "pending" };
                // Display must finish before creating an assistant Moment. Voice has its own receipt.
                JsonElement shown;
                // Once a physical send begins, finish collecting its bounded receipt under the
                // runtime gate even if the browser disconnects. Do not release/rebind mid-playback.
                object display=null;
                if(owner.companionDisplay)
                {
                    string image=null;
                    var match=context.Workspace.ExpressionMatch;
                    if(match?.status=="matched"&&!string.IsNullOrEmpty(match.selectedId))
                    {
                        try {image=Convert.ToBase64String(context.Services.Expressions.ReadImage(match.selectedId).bytes);}
                        catch {owner.Diagnostics.Add("display","image_unavailable","本轮表情图片不可用，保留状态画面。");}
                    }
                    var fields=new Dictionary<string,object>{["name"]=context.Services.Storage.LoadPairIdentity().Assname??"Soul",["subtitle"]=text};
                    if(image!=null)fields["image"]=image;
                    display=fields;
                }
                try { shown = owner.companionDisplay
                    ?await owner.Request("api/display",new {state="thinking",name=context.Services.Storage.LoadPairIdentity().Assname??"Soul",subtitle=""},CancellationToken.None)
                    :await owner.Request("api/text", new { text }, CancellationToken.None); }
                catch { owner.displayState.Finished(displayTicket,false);owner.delivery = new { outputId, display = "failed_or_unknown", audio = "not_started" }; throw; }
                var audioStatus = "completed";
                JsonElement audio = default;
                try { audio = await owner.SpeakAsync(text, CancellationToken.None,display:display,displayTicket:displayTicket); }
                catch (Exception)
                {
                    audioStatus = "failed_or_unknown";
                    if(owner.companionDisplay)
                    {
                        owner.delivery=new {outputId,display="failed_or_unknown",audio=audioStatus};
                        throw new InvalidOperationException("WXXZ 字幕或播放未完成，未记录完整回复、未重发。");
                    }
                }
                owner.delivery = new { outputId, display = "completed", audio = audioStatus,
                    displayReceipt = owner.companionDisplay ? (object)audio.GetProperty("display_receipts") : shown.GetProperty("receipt"),
                    audioReceipt = audio.ValueKind == JsonValueKind.Object ? (object)audio : null };
                context.Services.Storage.SaveOperationalEvent(new OperationalEventRecord
                {
                    Id = Guid.NewGuid().ToString("N"), ConversationId = context.ConversationId,
                    SourcePluginId = Id, SourceEventId = outputId, TraceId = context.TraceId,
                    Kind = OperationalEventKindValues.PluginRuntime, Role = "system_event",
                    Content = "WXXZ 屏幕完成；语音：" + audioStatus + "（" + owner.VoiceDescription + "）。",
                    PayloadJson = TraceJson.ToJson(owner.delivery), EnvironmentJson = TraceJson.ToJson(context.Environment),
                    CreatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                return new TraceCapabilityResultData
                {
                    Status = "success", Summary = "WXXZ 屏幕已显示；语音：" + audioStatus + "。", Payload = text,
                    ProducedEvent = new PluginEventData { PluginId = Id, ExternalEventId = outputId,
                        Role = context.Services.Storage.LoadPairIdentity().Assname, Content = text,
                        Realm = TraceRealmValues.SharedScene, EvidenceType = EvidenceTypeValues.AssPerformed,
                        PayloadJson = TraceJson.ToJson(new { outputId, displayed = true, audio = audioStatus }),
                        OccurredUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                };
            }
        }
    }
}
