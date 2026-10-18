// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using RynthCore.Plugin.RynthOracle.Data;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// The 100 exploration markers: rows of the Quests tab's merged list since 2026-10-18 (QuestsTab.cs:
/// MRK rows, markers found and the next one in the trackers' status). What is left here is the chat print.
/// </summary>
internal sealed partial class OracleView
{
    private void PrintMarker(Marker m) =>
        PrintEntry($"Marker {m.Number}: {m.Name}", "", m.IsFound(_c.Flags) ? "found" : "", m.Hint, m.Url);
}
