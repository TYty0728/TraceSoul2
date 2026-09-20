using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TraceSoul2.Data;
using TraceSoul2.ExternalPlugins.MediaUnderstanding;
using TraceSoul2.ExternalPlugins.RealtimeCall;
using TraceSoul2.Plugins;

if (args.FirstOrDefault() == "--command-fixture")
{
    Console.Write(JsonSerializer.Serialize(args.Skip(1).ToArray()));
    return;
}
if (args.FirstOrDefault() == "--command-output")
{
    Console.Write(new string('x', 20_000));
    return;
}
if (args.FirstOrDefault() == "--command-delay")
{
    await Task.Delay(TimeSpan.FromSeconds(10));
    return;
}

var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + label);
    checks++;
}
void Error(Action action, string expected)
{
    try { action(); throw new InvalidOperationException("Expected " + expected); }
    catch (CallProtocolException ex) { Check(ex.Code == expected, expected); }
}
JsonElement Read(string value) { using var d = JsonDocument.Parse(value); return d.RootElement.Clone(); }

// Real share text and hostile URLs: parsing alone must never imply content analysis.
var sources = MediaSourceParser.Parse("看看这个 https://www.bilibili.com/video/BV1xx411c7mD/?p=2&share_source=copy 。再看 https://v.douyin.com/abc_123/ 复制此链接打开抖音");
Check(sources.Count == 2 && sources[0].Part == 2, "share text and B站 part");
Check(sources[0].CanonicalUrl == "https://www.bilibili.com/video/BV1xx411c7mD?p=2", "tracking query removed");
Check(sources[1].RequiresResolution && sources[1].MediaId == "", "shortlink unresolved");
Check(MediaSourceParser.Parse("https://b23.tv/xyz/ https://b23.tv/xyz/").Count == 1, "dedup");
Check(MediaSourceParser.Parse("https://www.douyin.com/video/1234567890123456789")[0].Kind == "video", "douyin video");
Check(MediaSourceParser.Parse("https://www.douyin.com/note/123")[0].Kind == "image_post", "douyin image post");
foreach (var url in new[]
{
    "https://www.bilibili.com.evil.test/video/BV1xx411c7mD", "https://www.bilibili.com@evil.test/video/BV1xx411c7mD",
    "https://evil@www.bilibili.com/video/BV1xx411c7mD", "https://127.0.0.1/video/BV1xx411c7mD",
    "https://www.bilibili.com:9999/video/BV1xx411c7mD", "file:///video/BV1xx411c7mD",
    "https://www.bilibili.com/video/BV1xx411c7mD?p=0", "https://www.bilibili.com/video/BV1xx411c7mD?p=broken",
    "https://www.douyin.com/video/not-an-id", "https://v.douyin.com/abc/extra"
}) Check(MediaSourceParser.ParseUrl(url) == null, "reject unsupported/forged URL");

// Video analysis pipeline: sampling, evidence, cleanup, and failure boundaries work without network/model calls.
var points = FfmpegVideoFrameExtractor.SamplingPoints(10_000, 4);
Check(points.Count == 4 && points.SequenceEqual(points.OrderBy(x => x)) && points.Distinct().Count() == 4,
    "frame sampling is monotonic and unique");
Check(points[0] >= 0 && points[^1] < 10_000, "frame sampling stays in media range");
Check(FfmpegVideoFrameExtractor.SamplingPoints(500, 8).SequenceEqual(new[] { 0L }), "short video sample");
var subtitleCues = SubtitleParser.Parse("WEBVTT\n\n00:00:01.000 --> 00:00:02.500\n<c.green>第一句</c>\n\n00:03,000 --> 00:04,000\n第二句 &amp; 说明\n");
Check(subtitleCues.Count == 2 && subtitleCues[0].StartMs == 1000 && subtitleCues[0].EndMs == 2500,
    "VTT/SRT subtitle timestamps parsed");
Check(subtitleCues[0].Text == "第一句" && subtitleCues[1].Text == "第二句 & 说明",
    "subtitle tags and entities cleaned");
var mediaSource = MediaSourceParser.ParseUrl("https://www.bilibili.com/video/BV1xx411c7mD")!;
var cleanupCount = 0;
var fakeFrames = new[]
{
    new ExtractedVideoFrame { TimestampMs = 500, Bytes = new byte[] { 0xff, 0xd8, 0xff, 1 } },
    new ExtractedVideoFrame { TimestampMs = 9_750, Bytes = new byte[] { 0xff, 0xd8, 0xff, 2 } }
};
var fakePipeline = new MediaVideoPipeline(
    new FakeAcquirer(() => cleanupCount++), new FakeExtractor(fakeFrames), new FakeAnalyzer("画面从室内切换到室外。"));
var fakeResult = await fakePipeline.AnalyzeAsync(mediaSource, "发生了什么", null!, CancellationToken.None);
Check(fakeResult.Status == "visual_analyzed" && fakeResult.Summary.Contains("室外"), "pipeline returns model answer");
Check(fakeResult.Evidence.Count(x => x.Kind == MediaEvidenceKinds.VideoFrame) == 2 &&
      fakeResult.Evidence.Any(x => x.Kind == MediaEvidenceKinds.PageMetadata), "pipeline returns timed evidence");
Check(fakeResult.Limitations.Any(x => x.Contains("离散画面")) &&
      fakeResult.Limitations.Any(x => x.Contains("音轨")), "pipeline states coverage limits");
Check(cleanupCount == 1, "pipeline cleans acquired asset after success");
try
{
    var failedPipeline = new MediaVideoPipeline(
        new FakeAcquirer(() => cleanupCount++), new ThrowingExtractor(), new FakeAnalyzer("unused"));
    await failedPipeline.AnalyzeAsync(mediaSource, "", null!, CancellationToken.None);
    throw new InvalidOperationException("Expected extractor failure");
}
catch (MediaPipelineException ex)
{
    Check(ex.Code == "invalid_video" && cleanupCount == 2, "pipeline cleans acquired asset after failure");
}
var commandRunner = new ExternalCommandRunner();
var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process path");
string[] ChildArguments(params string[] values) =>
    string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
        ? new[] { Assembly.GetExecutingAssembly().Location }.Concat(values).ToArray()
        : values;
var commandResult = await commandRunner.RunAsync(executable,
    ChildArguments("--command-fixture", "value with spaces", "$(must-not-run)", "semi;colon"),
    Environment.CurrentDirectory, TimeSpan.FromSeconds(10), CancellationToken.None, 4096);
var commandArguments = JsonSerializer.Deserialize<string[]>(commandResult.StandardOutput)!;
Check(commandResult.ExitCode == 0 && commandArguments.SequenceEqual(new[]
    { "value with spaces", "$(must-not-run)", "semi;colon" }), "external commands preserve literal arguments without a shell");
var boundedOutput = await commandRunner.RunAsync(executable, ChildArguments("--command-output"),
    Environment.CurrentDirectory, TimeSpan.FromSeconds(10), CancellationToken.None, 1024);
Check(boundedOutput.StandardOutput.Length == 1024 && boundedOutput.OutputTruncated,
    "external command output is bounded while fully drained");
try
{
    await commandRunner.RunAsync(executable, ChildArguments("--command-delay"), Environment.CurrentDirectory,
        TimeSpan.FromMilliseconds(150), CancellationToken.None, 1024);
    throw new InvalidOperationException("Expected process timeout");
}
catch (MediaPipelineException ex)
{
    Check(ex.Code == "process_timeout", "external command timeout terminates child process");
}

var registry = new RealtimeSessionRegistry();
var session = registry.Start("owner", "conversation", "unity");
Error(() => registry.Get("stranger", session.SessionId), "session_not_found");
Error(() => registry.Start("other", "conversation", "qq_av_bridge"), "conversation_busy");
session.State = "fake";
Check(registry.Get("owner", session.SessionId).State == "connected", "snapshots immutable");
Check(registry.TryRegisterOutput(session.SessionId, 0, "answer", 1000), "register backend output");
Check(!registry.TryRegisterOutput(session.SessionId, 0, "answer", 1000), "output id dedup");
registry.ReportPlayback("owner", session.SessionId, 0, "answer", 300, false);
Error(() => registry.ReportPlayback("owner", session.SessionId, 0, "answer", 500, true), "invalid_playback_progress");
Error(() => registry.ReportPlayback("owner", session.SessionId, 0, "answer", 200, false), "invalid_playback_progress");
Error(() => registry.ReportPlayback("owner", session.SessionId, 0, "invented", 1000, true), "unknown_output");
Check(registry.Interrupt("owner", session.SessionId, 0).Generation == 1, "interrupt generation");
Check(!registry.TryRegisterOutput(session.SessionId, 0, "late", 1000), "late generated output dropped");
Error(() => registry.ReportPlayback("owner", session.SessionId, 0, "answer", 1000, true), "stale_generation");
var receipt = registry.Receipts("owner", session.SessionId).Single();
Check(receipt.PlayedMs == 300 && receipt.Interrupted && !receipt.Completed, "only confirmed prefix retained");
Check(registry.TryRegisterOutput(session.SessionId, 1, "new", 800), "new generation allowed");
registry.ReportPlayback("owner", session.SessionId, 1, "new", 800, true);
registry.End("owner", session.SessionId);
Check(registry.End("owner", session.SessionId).Generation == 2, "end idempotent");
Error(() => registry.Interrupt("owner", session.SessionId, 2), "session_ended");
Check(!registry.TryRegisterOutput(session.SessionId, 2, "after_end", 1000), "ended output blocked");
registry.Disconnect("owner");
Check(registry.ActiveCount == 0, "disconnect cleanup");
Error(() => registry.Get("owner", session.SessionId), "session_not_found");

// Concurrent attempts for one conversation cannot create two active calls.
var raceRegistry = new RealtimeSessionRegistry();
var wins = 0;
Parallel.For(0, 20, i =>
{
    try { raceRegistry.Start("owner" + i, "same", "debug"); Interlocked.Increment(ref wins); }
    catch (CallProtocolException e) when (e.Code == "conversation_busy") { }
});
Check(wins == 1 && raceRegistry.ActiveCount == 1, "atomic start");
raceRegistry.Stop();
Error(() => raceRegistry.Start("owner", "new", "debug"), "service_stopped");

// Replay cache, invalid fields and isolation of two real protocol connections.
using (var control = new CallControlConnection(registry, "configured-conversation"))
{
    Check(Read(control.Handle("{\"seq\":\"1\",\"op\":\"start\"}")).GetProperty("error").GetString() == "invalid_sequence", "wrong seq type");
    Check(Read(control.Handle("[]")).GetProperty("error").GetString() == "invalid_sequence", "wrong root type");
    Check(Read(control.Handle("{")).GetProperty("error").GetString() == "invalid_json", "malformed JSON");
    var startRequest = "{\"seq\":1,\"op\":\"start\",\"transport\":\"unity\"}";
    var first = control.Handle(startRequest);
    Check(control.Handle(startRequest) == first && registry.ActiveCount == 1, "replayed start");
    var id = Read(first).GetProperty("data").GetProperty("session_id").GetString()!;
    Check(Read(first).GetProperty("data").GetProperty("conversation_id").GetString() == "configured-conversation", "server binding");
    var interruption = JsonSerializer.Serialize(new { seq = 2, op = "interrupt", session_id = id, generation = 0 });
    var interrupted = control.Handle(interruption);
    Check(control.Handle(interruption) == interrupted && Read(interrupted).GetProperty("data").GetProperty("generation").GetInt64() == 1, "replayed interrupt");
    Check(Read(control.Handle("{\"seq\":2,\"op\":\"hello\"}")).GetProperty("error").GetString() == "sequence_conflict", "seq reused differently");
    using var stranger = new CallControlConnection(registry, "other-conversation");
    Check(Read(stranger.Handle(JsonSerializer.Serialize(new { seq = 1, op = "status", session_id = id }))).GetProperty("error").GetString() == "session_not_found", "protocol ownership");
    Check(Read(stranger.Handle("{\"seq\":2,\"op\":\"start\",\"transport\":\"unity\",\"conversation_id\":\"victim\"}")).GetProperty("error").GetString() == "conversation_bound_by_server", "client cannot pick memory");
    Check(Read(control.Handle(JsonSerializer.Serialize(new { seq = 3, op = "interrupt", session_id = id, generation = "bad" }))).GetProperty("error").GetString() == "invalid_generation", "wrong generation type");
    Check(Read(control.Handle("{\"seq\":4,\"op\":\"audio\"}")).GetProperty("error").GetString() == "unsupported_operation", "audio not falsely accepted");
    for (var seq = 5; seq < 140; seq++) control.Handle(JsonSerializer.Serialize(new { seq, op = "ping" }));
    Check(Read(control.Handle(startRequest)).GetProperty("error").GetString() == "stale_sequence", "evicted seq never replayed as new");
}
Check(registry.ActiveCount == 0, "protocol dispose cleanup");

// Transcript/TTS bridge: final ASR enters as user speech; only confirmed playback becomes assistant memory.
var dialogueRegistry = new RealtimeSessionRegistry();
var dialogueHub = new CallDialogueHub(dialogueRegistry);
var pushed = new List<string>();
using (var dialogueConnection = new CallControlConnection(dialogueRegistry, "call-conversation", dialogueHub,
           (value, _) => { pushed.Add(value); return Task.CompletedTask; }))
{
    var helloData = Read(dialogueConnection.Handle("{\"seq\":1,\"op\":\"hello\"}"))
        .GetProperty("data");
    Check(helloData.GetProperty("dialogue_ready").GetBoolean() &&
          helloData.GetProperty("stage").GetString() == "transcript_bridge", "dialogue bridge advertised honestly");
    var start = Read(dialogueConnection.Handle("{\"seq\":2,\"op\":\"start\",\"transport\":\"unity\"}"));
    var callId = start.GetProperty("data").GetProperty("session_id").GetString()!;
    var transcript = JsonSerializer.Serialize(new
        { seq = 3, op = "transcript", session_id = callId, generation = 0, text = "你好，能听见吗" });
    Check(Read(dialogueConnection.Handle(transcript)).GetProperty("ok").GetBoolean(), "final transcript accepted");
    var inboundSpeech = dialogueHub.TakeInbound().Single();
    Check(inboundSpeech.Role == "user" && inboundSpeech.Organ == BodyOrganValues.Voice &&
          inboundSpeech.Content.Contains("能听见"), "transcript becomes voice moment");

    var outgoing = await dialogueHub.SendAssistantAsync("call-conversation", "听得见，我们继续。", CancellationToken.None);
    Check(pushed.Count == 1 && Read(pushed[0]).GetProperty("op").GetString() == "assistant_text",
        "assistant text pushed for client TTS");
    Check(dialogueHub.TakeInbound().Count == 0, "unplayed answer is not semantic memory");
    var ready = JsonSerializer.Serialize(new
        { seq = 4, op = "output_ready", session_id = callId, generation = 0, output_id = outgoing.OutputId, duration_ms = 1200 });
    Check(Read(dialogueConnection.Handle(ready)).GetProperty("ok").GetBoolean(), "client TTS duration confirmed");
    var played = JsonSerializer.Serialize(new
        { seq = 5, op = "playback", session_id = callId, generation = 0, output_id = outgoing.OutputId, played_ms = 1200, completed = true });
    Check(Read(dialogueConnection.Handle(played)).GetProperty("ok").GetBoolean(), "complete playback accepted");
    var heard = dialogueHub.TakeInbound().Single();
    Check(heard.Role == "assistant" && heard.Content == "听得见，我们继续。", "completed playback becomes assistant memory");

    var partial = await dialogueHub.SendAssistantAsync("call-conversation", "一二三四", CancellationToken.None);
    dialogueConnection.Handle(JsonSerializer.Serialize(new
        { seq = 6, op = "output_ready", session_id = callId, generation = 0, output_id = partial.OutputId, duration_ms = 1000 }));
    dialogueConnection.Handle(JsonSerializer.Serialize(new
        { seq = 7, op = "playback", session_id = callId, generation = 0, output_id = partial.OutputId, played_ms = 500, completed = false }));
    var interrupted = Read(dialogueConnection.Handle(JsonSerializer.Serialize(new
        { seq = 8, op = "interrupt", session_id = callId, generation = 0 })));
    Check(interrupted.GetProperty("ok").GetBoolean() &&
          interrupted.GetProperty("data").GetProperty("generation").GetInt64() == 1, "dialogue interruption advances generation");
    var heardPrefix = dialogueHub.TakeInbound().Single();
    Check(heardPrefix.Role == "assistant" && heardPrefix.Content == "一二", "only played prefix survives interruption");
    var disconnectPartial = await dialogueHub.SendAssistantAsync("call-conversation", "甲乙丙丁", CancellationToken.None);
    dialogueConnection.Handle(JsonSerializer.Serialize(new
        { seq = 9, op = "output_ready", session_id = callId, generation = 1, output_id = disconnectPartial.OutputId, duration_ms = 1000 }));
    dialogueConnection.Handle(JsonSerializer.Serialize(new
        { seq = 10, op = "playback", session_id = callId, generation = 1, output_id = disconnectPartial.OutputId, played_ms = 500, completed = false }));
}
Check(dialogueRegistry.ActiveCount == 0 && dialogueHub.ActiveSessions == 0 && dialogueHub.HasPendingInbound,
    "dialogue disconnect cleanup preserves played prefix for polling");
Check(dialogueHub.TakeInbound().Single().Content == "甲乙", "disconnect archives only played prefix");

// Isolated loopback Kestrel exercises the actual endpoint, not the user's Host or QQ.
const string accessToken = "test-only-0123456789abcdefghijklmn";
var socketRegistry = new RealtimeSessionRegistry();
using var endpoint = new CallControlEndpoint(socketRegistry, "test", accessToken, TimeSpan.FromSeconds(5));
using var idleEndpoint = new CallControlEndpoint(new RealtimeSessionRegistry(), "idle", accessToken, TimeSpan.FromMilliseconds(200));
using var unconfigured = new CallControlEndpoint(new RealtimeSessionRegistry(), "none", "");
Check(!unconfigured.Accept("Bearer " + accessToken, ""), "fail closed without token");
Check(!endpoint.Accept("", "?access_token=" + accessToken), "query token rejected");
Check(!endpoint.Accept("Bearer wrong", ""), "wrong token rejected");
Check(endpoint.Accept("Bearer " + accessToken, ""), "header token accepted");
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app = builder.Build();
app.UseWebSockets();
app.Run(async context =>
{
    var target = context.Request.Path == "/idle" ? idleEndpoint : endpoint;
    if (!target.Accept(context.Request.Headers.Authorization.ToString(), context.Request.QueryString.Value ?? ""))
    {
        context.Response.StatusCode = 401; return;
    }
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    await target.OnConnectedAsync(ws, context.RequestAborted);
});
await app.StartAsync();
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
var uri = new Uri(address.Replace("http://", "ws://") + endpoint.Path);
async Task<ClientWebSocket> Connect(Uri url)
{
    var client = new ClientWebSocket();
    client.Options.SetRequestHeader("Authorization", "Bearer " + accessToken);
    await client.ConnectAsync(url, deadline.Token);
    return client;
}
async Task<string> Receive(ClientWebSocket client)
{
    var buffer = new byte[16384];
    var read = await client.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
    return read.MessageType == WebSocketMessageType.Close ? "CLOSED" : Encoding.UTF8.GetString(buffer, 0, read.Count);
}
async Task Send(ClientWebSocket client, string text)
{
    await client.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
}
using (var http = new HttpClient()) Check((await http.GetAsync(address + endpoint.Path)).StatusCode == HttpStatusCode.Unauthorized, "HTTP handshake denial");
using (var client = await Connect(uri))
{
    var hello = Encoding.UTF8.GetBytes("{\"seq\":1,\"op\":\"hello\"}");
    await client.SendAsync(hello.AsMemory(0, 8), WebSocketMessageType.Text, false, deadline.Token);
    await client.SendAsync(hello.AsMemory(8), WebSocketMessageType.Text, true, deadline.Token);
    var data = Read(await Receive(client)).GetProperty("data");
    Check(data.GetProperty("protocol").GetString() == "realtime.call.v1" && !data.GetProperty("audio_ready").GetBoolean(), "fragmented hello and honest capabilities");
    await Send(client, "{\"seq\":2,\"op\":\"start\",\"transport\":\"unity\"}");
    Check(Read(await Receive(client)).GetProperty("ok").GetBoolean(), "WS start");
    await client.SendAsync(new byte[5].AsMemory(), WebSocketMessageType.Binary, true, deadline.Token);
    Check(await Receive(client) == "CLOSED", "binary input rejected");
}
using (var client = await Connect(uri))
{
    await Send(client, new string('x', CallControlEndpoint.MaxMessageBytes + 1));
    Check(await Receive(client) == "CLOSED", "size limit");
}
using (var client = await Connect(new Uri(address.Replace("http://", "ws://") + "/idle")))
{
    try { await Receive(client); throw new InvalidOperationException("idle connection stayed open"); }
    catch (WebSocketException) { Check(true, "idle connection aborted"); }
}
using (var client = await Connect(uri))
{
    await Send(client, "{\"seq\":1,\"op\":\"hello\"}");
    await Receive(client);
    endpoint.Dispose();
    try { await Receive(client); }
    catch (WebSocketException) { }
    Check(socketRegistry.ActiveCount == 0 && !endpoint.Accept("Bearer " + accessToken, ""), "unload rejects and clears");
}
await app.StopAsync(deadline.Token);
Check(endpoint.ActiveConnections == 0, "all sockets cleaned up");
Console.WriteLine($"PASS: {checks} media/realtime checks (no external API or QQ calls).");

sealed class FakeAcquirer : IMediaVideoAcquirer
{
    private readonly Action cleanup;
    public FakeAcquirer(Action cleanup) => this.cleanup = cleanup;
    public Task<AcquiredMediaAsset> AcquireAsync(MediaSourceData source, CancellationToken cancellationToken) =>
        Task.FromResult(new AcquiredMediaAsset
        {
            FilePath = "fake.mp4", Title = "测试视频", Author = "测试作者", OnDispose = cleanup
        });
}

sealed class FakeExtractor : IVideoFrameExtractor
{
    private readonly IReadOnlyList<ExtractedVideoFrame> frames;
    public FakeExtractor(IReadOnlyList<ExtractedVideoFrame> frames) => this.frames = frames;
    public Task<(long DurationMs, IReadOnlyList<ExtractedVideoFrame> Frames)> ExtractAsync(
        AcquiredMediaAsset asset, CancellationToken cancellationToken) =>
        Task.FromResult((10_000L, frames));
}

sealed class ThrowingExtractor : IVideoFrameExtractor
{
    public Task<(long DurationMs, IReadOnlyList<ExtractedVideoFrame> Frames)> ExtractAsync(
        AcquiredMediaAsset asset, CancellationToken cancellationToken) =>
        throw new MediaPipelineException("invalid_video");
}

sealed class FakeAnalyzer : IMediaFrameAnalyzer
{
    private readonly string answer;
    public FakeAnalyzer(string answer) => this.answer = answer;
    public Task<string> AnalyzeAsync(string question, long durationMs, IReadOnlyList<ExtractedVideoFrame> frames,
        IReadOnlyList<MediaEvidenceData> subtitles, TraceTurnContext turn, CancellationToken cancellationToken) =>
        Task.FromResult(answer);
}
