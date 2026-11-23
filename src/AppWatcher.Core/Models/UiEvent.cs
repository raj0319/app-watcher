using System.Text.Json.Serialization;

namespace AppWatcher.Core.Models;

/// <summary>One line of events/&lt;date&gt;.jsonl.</summary>
public sealed class UiEvent
{
    [JsonPropertyName("t")] public DateTime T { get; set; }
    /// <summary>Title of the foreground window of the watched app at that moment (for tab documents: "Window › Tab").</summary>
    [JsonPropertyName("win")] public string Win { get; set; } = "";
    [JsonPropertyName("ct")] public string Ct { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("aid")] public string Aid { get; set; } = "";
    /// <summary>focus | open | close | invoke | select | value | foreground | dialog | dropdown | result | start | stop</summary>
    [JsonPropertyName("act")] public string Act { get; set; } = "";
    [JsonPropertyName("val")] public string? Val { get; set; }
    /// <summary>Milliseconds since the previous recorded event.</summary>
    [JsonPropertyName("dt")] public long Dt { get; set; }
    /// <summary>Effects file written for this event, if any.</summary>
    [JsonPropertyName("fx")] public string? Fx { get; set; }
    /// <summary>For invoke events: how the press was seen. uia | msaa | menu | inferred | toggle | select. Null for the classic UIA Invoke event.</summary>
    [JsonPropertyName("how")] public string? How { get; set; }
    /// <summary>For dropdown events: the suggestions shown (masked like typed values). For result events: the grid's header then its first rows, cells joined by " | ".</summary>
    [JsonPropertyName("items")] public List<string>? Items { get; set; }
    /// <summary>Picture of the screen state right after this event, relative to the screens folder ("shots/&lt;screen&gt;/03-after-press-save.png").</summary>
    [JsonPropertyName("shot")] public string? Shot { get; set; }
    /// <summary>For value events: "app" when the program filled the value in itself (the field did not have keyboard focus); null when the user typed it.</summary>
    [JsonPropertyName("by")] public string? By { get; set; }
    /// <summary>For grid cell values: the row the cell belongs to ("line 1" by the line number cell, else "row 0"); null for fields.</summary>
    [JsonPropertyName("row")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Row { get; set; }

    /// <summary>"events/2026-01-01.jsonl#L12" - set by the reader, never serialized.</summary>
    [JsonIgnore] public string Source { get; set; } = "";
    /// <summary>Collector only, never written: a value from an editor lying over a grid cell (the editor names the grid, not the column).</summary>
    [JsonIgnore] public bool OverGridCell { get; set; }
    /// <summary>Character keys seen going down since the focus came to the field (collector only; never written).</summary>
    [JsonIgnore] public int KeysSeen { get; set; }

    /// <summary>
    /// The name a person would use for the control: its Name, else its cleaned AutomationId, else the control type. A Name
    /// that is itself data (digits, a phone, "City, ST #####", an address) is replaced by the cleaned automation id or its
    /// shape; a tab item named after a document ("Vendor - 00283 "ACME" ...") goes through the title masks.
    /// </summary>
    [JsonIgnore] public string FieldKey
    {
        get
        {
            var name = Name ?? "";
            if (name.Length > 0 && Ct == "TabItem") return Learning.Titles.Key(name);
            if (name.Length > 0 && !Masking.LooksLikeData(name)) return name;
            var cleaned = Masking.LooksLikeData(Aid) ? "" : Screens.Labels.Clean(Aid);
            if (cleaned.Length > 0) return cleaned;
            if (name.Length > 0) return Masking.Shape(name);
            return string.IsNullOrEmpty(Aid) || Masking.LooksLikeData(Aid) ? Ct : Aid;
        }
    }
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
    /// <summary>A suggestion list or lookup popup appeared while a field had focus: Name = field, Val = text typed so far, Items = suggestions.</summary>
    public const string Dropdown = "dropdown";
    /// <summary>A grid's content right after an action: Name = the action, Val = grid name, Items = header and first rows.</summary>
    public const string Result = "result";
    /// <summary>A menu opened: Name = the menu it belongs to (when known), Items = its items. Shows what exists even if nobody chooses it.</summary>
    public const string Menu = "menu";
    /// <summary>A hover tip the program showed: Name = the control under the pointer, Val = the tip's text.</summary>
    public const string Hint = "hint";
    public const string Start = "start";
    public const string Stop = "stop";
}

/// <summary>Who changed a value (UiEvent.By).</summary>
public static class ValueBy
{
    public const string App = "app";
}

/// <summary>How an invoke event was observed (UiEvent.How).</summary>
public static class PressHow
{
    public const string Uia = "uia";
    public const string Msaa = "msaa";
    public const string Menu = "menu";
    public const string Inferred = "inferred";
    public const string Toggle = "toggle";
    public const string Select = "select";
}
