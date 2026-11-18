using System.Xml;
using AppWatcher.Core.Learning;
using AppWatcher.Core.Models;
using AppWatcher.Core.Snapshots;

namespace AppWatcher.Core.Export;

/// <summary>
/// Writes an XES event log (IEEE 1849) for process-mining tools such as PM4Py, ProM or Disco:
/// one trace per user session, one event per window visit and per save action.
/// Activity names are window titles with numbers collapsed; no typed values are included.
/// </summary>
public static class XesWriter
{
    public static int Write(string path, List<UiEvent> events, List<EffectRecord> effects, string appName)
    {
        var sessions = FlowLearner.Sessions(events);
        var settings = new XmlWriterSettings { Indent = true, Encoding = new System.Text.UTF8Encoding(false) };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = XmlWriter.Create(path, settings);
        w.WriteStartDocument();
        w.WriteStartElement("log", "http://www.xes-standard.org/");
        w.WriteAttributeString("xes.version", "1849.2016");
        w.WriteAttributeString("xes.features", "nested-attributes");
        Ext(w, "Concept", "concept", "http://www.xes-standard.org/concept.xesext");
        Ext(w, "Time", "time", "http://www.xes-standard.org/time.xesext");
        Ext(w, "Lifecycle", "lifecycle", "http://www.xes-standard.org/lifecycle.xesext");
        Ext(w, "Organizational", "org", "http://www.xes-standard.org/org.xesext");
        w.WriteStartElement("global"); w.WriteAttributeString("scope", "trace"); Str(w, "concept:name", ""); w.WriteEndElement();
        w.WriteStartElement("global"); w.WriteAttributeString("scope", "event");
        Str(w, "concept:name", ""); Str(w, "lifecycle:transition", "complete"); Date(w, "time:timestamp", DateTime.MinValue); w.WriteEndElement();
        w.WriteStartElement("classifier"); w.WriteAttributeString("name", "Activity"); w.WriteAttributeString("keys", "concept:name"); w.WriteEndElement();
        Str(w, "concept:name", $"App Watcher log for {appName}");
        Str(w, "source", "App Watcher");

        var traces = 0;
        var n = 0;
        foreach (var session in sessions)
        {
            var visits = FlowLearner.Visits(session);
            if (visits.Count == 0) continue;
            traces++;
            n++;
            w.WriteStartElement("trace");
            Str(w, "concept:name", $"session {n} ({session[0].T:yyyy-MM-dd HH:mm})");
            var items = new List<(DateTime t, string name, string kind, string? extra)>();
            foreach (var v in visits) items.Add((v.Start, v.Win, "screen", null));
            var start = session[0].T; var end = session[^1].T;
            foreach (var fx in effects.Where(f => f.T >= start && f.T <= end.AddSeconds(30)))
                items.Add((fx.T, $"save: {(fx.TriggerName.Length > 0 ? fx.TriggerName : string.Join("+", fx.Diff.Tables.Select(t => t.Table).Take(3)))}", "save", string.Join(", ", fx.TablesChanged)));
            foreach (var (t, name, kind, extra) in items.OrderBy(i => i.t))
            {
                w.WriteStartElement("event");
                Str(w, "concept:name", name);
                Str(w, "lifecycle:transition", "complete");
                Date(w, "time:timestamp", t);
                Str(w, "org:resource", "user");
                Str(w, "appwatcher:kind", kind);
                if (extra is not null) Str(w, "appwatcher:tables", extra);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndDocument();
        return traces;
    }

    private static void Ext(XmlWriter w, string name, string prefix, string uri)
    {
        w.WriteStartElement("extension"); w.WriteAttributeString("name", name); w.WriteAttributeString("prefix", prefix); w.WriteAttributeString("uri", uri); w.WriteEndElement();
    }
    private static void Str(XmlWriter w, string key, string value)
    {
        w.WriteStartElement("string"); w.WriteAttributeString("key", key); w.WriteAttributeString("value", value); w.WriteEndElement();
    }
    private static void Date(XmlWriter w, string key, DateTime value)
    {
        w.WriteStartElement("date"); w.WriteAttributeString("key", key); w.WriteAttributeString("value", value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK")); w.WriteEndElement();
    }
}
