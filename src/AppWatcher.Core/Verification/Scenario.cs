using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppWatcher.Core.Verification;

/// <summary>
/// One driven scenario: what a person (or the driver, on a test environment) does in the original program, step by
/// step, and what the watcher must have recorded for each step. The same file drives the original and scores the
/// collector; later it drives a replacement too. Kept small and plain so the test PC's scripts can be rewritten as files.
/// </summary>
public sealed class Scenario
{
    public string Name { get; set; } = "";
    /// <summary>The watched program's display name (the data folder is named after it).</summary>
    public string App { get; set; } = "";
    /// <summary>Wildcard of the main window's title ("DDI System Inform*").</summary>
    public string Window { get; set; } = "*";
    public string? Note { get; set; }
    public List<ScenarioStep> Steps { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public static Scenario Load(string path) => JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("not a scenario: " + path);
    public string Save() => JsonSerializer.Serialize(this, Json);
}

/// <summary>What a step does in the program.</summary>
public enum StepKind
{
    /// <summary>Click a toolbar button or a named control that opens a document or window (`target`).</summary>
    Open,
    /// <summary>Walk a menu by names (`path`: top menu, then items).</summary>
    Menu,
    /// <summary>Type `value` into the field `field` (automation id or name, wildcard), then Enter or Tab (`then`).</summary>
    Type,
    /// <summary>Press the button `target` (a real click).</summary>
    Press,
    /// <summary>Click a tab page or a navigation item named `target` inside the document.</summary>
    Nav,
    /// <summary>Rest the pointer on `target` for a tip.</summary>
    Hover,
    /// <summary>Answer the prompt titled `title` with the button `button`.</summary>
    Prompt,
    /// <summary>Wait `ms` milliseconds.</summary>
    Wait,
}

public sealed class ScenarioStep
{
    public StepKind Do { get; set; }
    public string? Target { get; set; }
    public List<string>? Path { get; set; }
    public string? Field { get; set; }
    public string? Value { get; set; }
    /// <summary>"enter" (default) or "tab" after typing.</summary>
    public string? Then { get; set; }
    public string? Title { get; set; }
    public string? Button { get; set; }
    public int? Ms { get; set; }
    /// <summary>A press that could change data (Save, Post, Delete, Send) runs only when the scenario says so here.</summary>
    public bool? Confirm { get; set; }
    /// <summary>What the watcher must record for this step; filled from the kind when absent (see <see cref="Expectation.For"/>).</summary>
    public Expectation? Expect { get; set; }

    public string Describe() => Do switch
    {
        StepKind.Open => $"open {Target}",
        StepKind.Menu => $"menu {string.Join(" > ", Path ?? new())}",
        StepKind.Type => $"type into {Field}",
        StepKind.Press => $"press {Target}",
        StepKind.Nav => $"nav {Target}",
        StepKind.Hover => $"hover {Target}",
        StepKind.Prompt => $"prompt {Title} -> {Button}",
        StepKind.Wait => $"wait {Ms} ms",
        _ => Do.ToString(),
    };
}

/// <summary>What the events must show within <see cref="WithinSeconds"/> of the step: an invoke by name, a select, a typed value, a dialog, a hint, a screen opening.</summary>
public sealed class Expectation
{
    /// <summary>An `invoke` event with this name.</summary>
    public string? Press { get; set; }
    /// <summary>A `select` event (tab item) with this name.</summary>
    public string? Select { get; set; }
    /// <summary>A `value` event typed by the user (no `by`) with this value.</summary>
    public string? Typed { get; set; }
    /// <summary>A `dialog` event whose window title matches this wildcard.</summary>
    public string? Dialog { get; set; }
    /// <summary>A `hint` event on this control. Tips are not always shown: a missing hint is "untested", not a failure.</summary>
    public string? Hint { get; set; }
    /// <summary>An `open` or `select` of a screen whose title matches this wildcard.</summary>
    public string? Screen { get; set; }
    public double WithinSeconds { get; set; } = 4;
    /// <summary>True when the step proves nothing by itself (a wait, a hover with no tip).</summary>
    public bool Optional { get; set; }

    /// <summary>The default expectation of a step kind.</summary>
    public static Expectation? For(ScenarioStep s) => s.Expect ?? s.Do switch
    {
        StepKind.Open => new Expectation { Press = s.Target, WithinSeconds = 6 },
        StepKind.Menu => new Expectation { Press = s.Path is { Count: > 0 } ? s.Path[^1] : null, WithinSeconds = 6 },
        StepKind.Type => new Expectation { Typed = s.Value, WithinSeconds = 6 },
        StepKind.Press => new Expectation { Press = s.Target },
        StepKind.Nav => new Expectation { Select = s.Target },
        StepKind.Hover => new Expectation { Hint = s.Target, Optional = true },
        StepKind.Prompt => new Expectation { Dialog = s.Title, Press = s.Button, WithinSeconds = 6 },
        _ => null,
    };
}

/// <summary>One executed step of a run, as the driver (or a legacy driver log) reports it.</summary>
public sealed class RunStep
{
    public DateTime T { get; set; }
    public int Index { get; set; }
    public ScenarioStep Step { get; set; } = new();
    /// <summary>done | blocked (the control or window was not found, nothing happened) | failed (the driver's own error)</summary>
    public string Status { get; set; } = "done";
    public string? Note { get; set; }
}
