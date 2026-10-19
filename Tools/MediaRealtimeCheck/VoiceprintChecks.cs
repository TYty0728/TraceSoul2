using System.Text.Json;
using TraceSoul2.Data;
using TraceSoul2.ExternalPlugins.RealtimeCall;
using TraceSoul2.Plugins;

internal static class VoiceprintChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tracesoul-voice-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "voiceprint.json");
        var same = new double[192]; same[0] = 1;
        var other = new double[192]; other[1] = 1;
        JsonElement Read(object value) => JsonSerializer.SerializeToElement(value);
        object Voice(double[] vector, bool playing = false) => new { model = VoiceprintGate.Model,
            playback_active = playing, windows = new[] { new { speech_ms = 1200, embedding = vector } } };
        void Enroll() => File.WriteAllText(path, JsonSerializer.Serialize(new
            { model = VoiceprintGate.Model, sample_count = 3, speech_ms = 18000, embedding = same }));
        try
        {
            var gate = new VoiceprintGate(path);
            check(gate.Status == "enrollment_required" && gate.RejectReason(Read(new { })) == "voiceprint_not_enrolled", "missing enrollment fails closed");
            Enroll();
            check(gate.Status == "ready", "profile hot loaded");
            check(gate.RejectReason(Read(new { voice = Voice(same) })) == null, "enrolled voice accepted");
            check(gate.RejectReason(Read(new { voice = Voice(other) })) == "speaker_mismatch", "other speaker rejected");
            check(gate.RejectReason(Read(new { voice = Voice(same, true) })) == "playback_echo_guard", "playback echo rejected");
            check(gate.RejectReason(Read(new { verified = true })) == "voice_evidence_required", "verified flag is not evidence");
            check(gate.RejectReason(Read(new { voice = new { model = "wrong", playback_active = false } })) == "voice_model_mismatch", "wrong model rejected");
            check(gate.RejectReason(Read(new { voice = new { model = VoiceprintGate.Model, playback_active = false,
                windows = new[] { new { speech_ms = 1200, embedding = same }, new { speech_ms = 1200, embedding = other } } } })) == "speaker_mismatch", "mixed speaker windows rejected");
            foreach (var ms in new object[] { 0, 799, 2501, "1200", null! })
                check(gate.RejectReason(Read(new { voice = new { model = VoiceprintGate.Model, playback_active = false,
                    windows = new[] { new { speech_ms = ms, embedding = same } } } })) == "insufficient_speech", "invalid speech duration rejected without exception");
            foreach (var embedding in new object[] { new double[192], new[] { 1.0 }, "bad", Enumerable.Repeat(1e100, 192).ToArray() })
                check(gate.RejectReason(Read(new { voice = new { model = VoiceprintGate.Model, playback_active = false,
                    windows = new[] { new { speech_ms = 1200, embedding } } } })) == "invalid_voice_evidence", "invalid vectors fail closed");

            var registry = new RealtimeSessionRegistry();
            var hub = new CallDialogueHub(registry, "voice-owner", true);
            using var connection = new CallControlConnection(registry, "voice-test", hub, (_, _) => Task.CompletedTask, gate);
            long sequence = 0;
            JsonElement Request(object value) => JsonSerializer.Deserialize<JsonElement>(connection.Handle(JsonSerializer.Serialize(value)));
            var hello = Request(new { seq = ++sequence, op = "hello" }).GetProperty("data");
            check(hello.GetProperty("voiceprint_required").GetBoolean() && !hello.GetProperty("silence_ends_call").GetBoolean(), "voice and silence capabilities advertised");
            var session = Request(new { seq = ++sequence, op = "start", transport = "debug" }).GetProperty("data").GetProperty("session_id").GetString()!;
            check(!Request(new { seq = ++sequence, op = "transcript", session_id = session, generation = 0, text = "电视对白", voice = Voice(other) })
                .GetProperty("data").GetProperty("accepted").GetBoolean() && !hub.HasPendingInbound, "rejected speech cannot wake model or enter memory");
            var accepted = new { seq = ++sequence, op = "transcript", session_id = session, generation = 0, text = "我说的话", voice = Voice(same) };
            Request(accepted); Request(accepted);
            var input = hub.TakeInbound().Single();
            check(input.Environment.SpeakerId == "voice-owner" && input.Environment.SessionId == session && input.Environment.ExclusivePair,
                "accepted voice carries server configured environment and is not duplicated");
            check(!input.PayloadJson.Contains("embedding"), "biometric evidence excluded from events");
            var before = Request(new { seq = ++sequence, op = "status", session_id = session });
            check(!Request(new { seq = ++sequence, op = "interrupt", session_id = session, generation = 0, voice = Voice(other) }).GetProperty("ok").GetBoolean(), "TV cannot auto interrupt");
            check(Request(new { seq = ++sequence, op = "status", session_id = session }).GetProperty("data").GetProperty("generation").GetInt64() == 0,
                "rejected interruption preserves generation");
            for (var i = 0; i < 300; i++) Request(new { seq = ++sequence, op = "ping" });
            check(hub.ActiveSessions == 1 && !hub.HasPendingInbound, "silent keepalive never creates a dialogue event");
            var context = new TraceTurnContext("voice-test", null!, new(), 0, false, null!,
                new EnvironmentSnapshotData { PlatformId = "realtime.call", Visibility = "private", SessionId = session, SpeakerId = "voice-owner" });
            check(hub.CanSpeak(context), "existing proactive wake can use bound call");
            check(!hub.CanSpeak(new TraceTurnContext("voice-test", null!, new(), 0, false, null!,
                new EnvironmentSnapshotData { PlatformId = "onebot", Visibility = "private", SessionId = session, SpeakerId = "voice-owner" })), "unrelated QQ wake cannot leak into call");
            check(!hub.CanSpeak(new TraceTurnContext("voice-test", null!, new(), 0, false, null!,
                new EnvironmentSnapshotData { PlatformId = "realtime.call", Visibility = "public", SessionId = session, SpeakerId = "voice-owner" })), "unconfirmed audience cannot receive private call reply");
            for (var i = 0; i < 600; i++)
            {
                var output = await hub.SendAssistantAsync("voice-test", "测试", CancellationToken.None);
                Request(new { seq = ++sequence, op = "output_ready", session_id = session, generation = 0, output_id = output.OutputId, duration_ms = 1000 });
                var receipt = Request(new { seq = ++sequence, op = "playback", session_id = session, generation = 0,
                    output_id = output.OutputId, played_ms = 1000, completed = true });
                if (!receipt.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("long call playback failed");
                if (hub.TakeInbound().Count != 1) throw new InvalidOperationException("long call receipt lost");
            }
            check(true, "600 outputs recycle completed receipts without ending call");
            check(Request(new { seq = ++sequence, op = "interrupt", session_id = session, generation = 0, reason = "manual" }).GetProperty("ok").GetBoolean(), "manual stop always available");
            check(!Request(new { seq = ++sequence, op = "transcript", session_id = session, generation = 0, text = "旧片段", voice = Voice(same) }).GetProperty("ok").GetBoolean(), "old verified transcript cannot bypass generation");
            File.Delete(path);
            check(gate.RejectReason(Read(new { voice = Voice(same) })) == "voiceprint_not_enrolled", "deleting enrollment immediately disables voice");
            File.WriteAllText(path, "{bad");
            check(gate.Status == "enrollment_required", "corrupt profile fails closed");
            check(new VoiceprintGate(null, false).RejectReason(Read(new { })) == null, "explicit legacy mode supported");
            Request(new { seq = ++sequence, op = "end", session_id = session });
            check(!hub.CanSpeak(context), "ended call cannot receive proactive speech");
        }
        finally { Directory.Delete(directory, true); }
    }
}
