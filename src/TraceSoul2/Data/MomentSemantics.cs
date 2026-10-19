using System;
using System.Text.Json;

namespace TraceSoul2.Data;

/// <summary>Persisted semantics. Labels are presentation; message text never selects its own type or route.</summary>
public static class MomentTypes
{
    public const string Message = "message";
    public const string Sight = "sight";
    public const string Hearing = "hearing";
    public const string Feeling = "feeling";
    public const string Touch = "touch";
    public const string Expression = "expression";
    public const string Event = "event";
    public const string PerceptionRole = "perception";
    public static readonly string[] All = { Message, Sight, Hearing, Feeling, Touch, Expression, Event };
    public static bool IsPerception(string type) => type is Sight or Hearing or Feeling or Touch;
    public static bool IsObservation(string type) => type is Sight or Feeling or Touch;
    public static string Normalize(string value)
    {
        var type = (value ?? "").Trim().ToLowerInvariant();
        if (Array.IndexOf(All, type) < 0) throw new ArgumentException("未知的Moment类型。");
        return type;
    }
    public static string Label(string type) => type switch
    {
        Message => "我收到消息", Sight => "我看见", Hearing => "我听见", Feeling => "我感受到",
        Touch => "我碰到", Expression => "我表达", _ => "发生事件"
    };
    public static string Platform(string value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "builtin.onebot" or "qq" => "qq",
        "builtin.dialogue" or "dialogue.receive" or "console" => "console",
        "wxxz" => "wxxz",
        "robot" => "robot",
        "builtin.time" or "night.residue" or "runtime.execution" => "system",
        "" => "unknown",
        var other => other
    };
    public static string PlatformLabel(string value) => Platform(value) switch
    {
        "qq" => "QQ", "wxxz" => "小智开发板", "console" => "控制台", "robot" => "小机器人",
        "system" => "系统", "unknown" => "未记录平台", var other => other
    };
    public static string InferType(MomentRecord moment, bool companion = false, bool human = false)
    {
        if (!string.IsNullOrWhiteSpace(moment.Type)) return Normalize(moment.Type);
        if (moment.EvidenceType == EvidenceTypeValues.AssPerformed || companion || moment.Role == "assistant") return Expression;
        // Backfill only trusted old board transcripts. A literal label in a text message proves nothing.
        if (moment.SourcePluginId == "wxxz" && BoardTranscript(moment.PayloadJson)) return Hearing;
        if (moment.EvidenceType == EvidenceTypeValues.DialogueExplicit || human || moment.Role == "user") return Message;
        return Event;
    }
    private static bool BoardTranscript(string payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload ?? "{}");
            return json.RootElement.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.String &&
                input.GetString() == "board_final_transcript";
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    public static string InferPlatform(MomentRecord moment)
    {
        if (!string.IsNullOrWhiteSpace(moment.SourcePlatform)) return Platform(moment.SourcePlatform);
        if (Platform(moment.SourcePluginId) == "system") return "system";
        try
        {
            using var json = JsonDocument.Parse(moment.EnvironmentJson ?? "{}");
            foreach (var property in json.RootElement.EnumerateObject())
                if (property.Name.Equals("PlatformId", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString())) return Platform(property.Value.GetString());
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        return Platform(moment.SourcePluginId);
    }
    public static string ForPrompt(MomentRecord moment)
    {
        if (moment == null) return "";
        var text = moment.Content ?? "";
        if (string.IsNullOrWhiteSpace(moment.Type) || moment.Type == Expression || moment.Type == Event) return text;
        var label = "【" + Label(moment.Type) + "】";
        // Historical board rows may already carry this trusted display prefix; don't double it.
        return text.StartsWith(label + "\n", StringComparison.Ordinal) ? text : label + "\n" + text;
    }
}
