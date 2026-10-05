// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>Markers: which of the 100 exploration markers you've found, and the next one to run to.</summary>
internal sealed partial class OracleView
{
    private const int MarkerPageSize = 50;
    private bool _markersMissingOnly;
    private int _markerPage;

    private void DrawMarkers(UiWindow w)
    {
        QuestFlagStore flags = _c.Flags;
        if (flags.LastReadUtc == DateTime.MinValue)
            w.TextDisabled("Quest flags not read yet (Quests tab, Refresh).");

        int found = 0;
        Marker? next = null;
        foreach (Marker m in MarkerList.All)
        {
            if (m.IsFound(flags)) found++;
            else next ??= m;
        }
        w.ProgressBar(MarkerList.All.Count > 0 ? (float)found / MarkerList.All.Count : 0f, -1f, 0f, $"{found} / {MarkerList.All.Count} markers found");
        if (next != null)
        {
            Marker n = next;
            w.TextDisabled("Next:");
            w.SameLine();
            w.Selectable($"#{next.Number} {next.Name} ({next.Location})", "m.next", false, () => PrintMarker(n));
            w.Tooltip("Prints the directions in chat.");
        }
        w.Checkbox("Only the ones still to find", "m.missing", _markersMissingOnly, v => { _markersMissingOnly = v; _markerPage = 0; });

        var rows = new System.Collections.Generic.List<Marker>();
        foreach (Marker m in MarkerList.All)
            if (!_markersMissingOnly || !m.IsFound(flags)) rows.Add(m);
        _markerPage = Pager(w, "m.page", _markerPage, rows.Count, MarkerPageSize, p => _markerPage = p);

        w.BeginChild("m.list", 0f, 0f, border: true);
        int start = _markerPage * MarkerPageSize;
        for (int i = start; i < rows.Count && i < start + MarkerPageSize; i++)
        {
            Marker m = rows[i];
            DoneMark(w, m.IsFound(flags));
            w.SameLine(50f);
            Marker row = m;
            w.Selectable(Cut($"#{m.Number} {m.Name}", 38), "m.r." + m.Number, false, () => PrintMarker(row), 300f);
            w.SameLine(360f);
            w.TextDisabled(Cut(m.Location, 24));
        }
        w.EndChild();
    }

    private void PrintMarker(Marker m) =>
        PrintEntry($"Marker {m.Number}: {m.Name}", "", m.IsFound(_c.Flags) ? "found" : "", m.Hint, m.Url);
}
