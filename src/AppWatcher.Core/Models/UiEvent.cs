using System.Text.Json.Serialization;

namespace AppWatcher.Core.Models;

/// <summary>One line of events/&lt;date&gt;.jsonl.</summary>
public sealed class UiEvent
{
    [JsonPropertyName("t")] public DateTime T { get; set; }
    /// <summary>Title of the foreground window of the watched app at that moment.</summary>
    [JsonPropertyName("win")] public string Win { get; set; } = "";
    [JsonPropertyName("ct")] public string Ct { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("aid")] public string Aid { get; set; } = "";
    /// <summary>focus | open | close | invoke | select | value | foreground | dialog | start | stop</summary>
    [JsonPropertyName("act")] public string Act { get; set; } = "";
    [JsonPropertyName("val")] public string? Val { get; set; }
    /// <summary>Milliseconds since the previous recorded event.</summary>
    [JsonPropertyName("dt")] public long Dt { get; set; }
    /// <summary>Effects file written for this event, if any.</summary>
    [JsonPropertyName("fx")] public string? Fx { get; set; }

    /// <summary>"events/2026-01-01.jsonl#L12" - set by the reader, never serialized.</summary>
    [JsonIgnore] public string Source { get; set; } = "";

    [JsonIgnore] public string FieldKey => string.IsNullOrEmpty(Name) ? (string.IsNullOrEmpty(Aid) ? Ct : Aid) : Name;
}

public static class Acts
{
    public const string Focus = "focus";
    public const string Open = "open";
    public const string Close = "close";
    public const string Invoke = "invoke";
    public const string Select = "select";
    public const string Value = "value";
    public const string Foreground = "foreground";
    public const string Dialog = "dialog";
    public const string Start = "start";
    public const string Stop = "stop";
}
