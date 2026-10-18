using System;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// Legendary: the Legendary Key quests John in Arwic counts (30 for his reward), by area: which
/// this character has done, which are missing, and the time left on each repeat timer. Read from
/// /myquests; the list and notes follow the "Arwic John Route" sheet (legendary.csv).
/// </summary>
internal sealed partial class OracleView
{
    private bool _legMissingOnly, _legReadyOnly, _legHideReady;

    private void DrawLegendary(UiWindow w)
    {
        QuestFlagStore flags = _c.Flags;
        DateTime now = _c.UtcNow;
        QuestFlagsBar(w, "lg", "legendary quests");

        int have = 0, ready = 0;
        long lumMissing = 0;
        foreach (LegendaryQuest q in LegendaryList.All)
        {
            TimeSpan? left = q.TimeLeft(flags, now);
            if (left == null) { lumMissing += q.Luminance; continue; }
            have++;
            if (left.Value <= TimeSpan.Zero) ready++;
        }

        // John's own counter when the server has it; it counts every key quest, not just these.
        int? john = LegendaryList.JohnCount(flags);
        int shown = john ?? have;
        w.ProgressBar(Math.Min(1f, (float)shown / LegendaryList.JohnTarget), -1f, 0f,
            john != null
                ? $"John: {john} / {LegendaryList.JohnTarget} legendary quests"
                : $"{have} / {LegendaryList.JohnTarget} toward John (his counter isn't on this character yet)");
        w.Tooltip("John in Arwic (33.6N 56.7E) rewards 30 different Legendary Key quests: 500,000 luminance and a 25-use Legendary Key.\n"
                  + "Bar: his counter (legendaryquestcounter_0913) when /myquests has it, else the quests below you've done.");
        w.Text($"{have} of {LegendaryList.All.Count} done here, {ready} ready to run again, {LegendaryList.All.Count - have} missing "
               + $"({TimeText.Number(lumMissing)} luminance not yet earned).");

        w.Checkbox("Missing only", "lg.missing", _legMissingOnly, v => { _legMissingOnly = v; if (v) { _legReadyOnly = false; _legHideReady = false; } });
        w.SameLine();
        w.Checkbox("Ready to run", "lg.ready", _legReadyOnly, v => { _legReadyOnly = v; if (v) { _legMissingOnly = false; _legHideReady = false; } });
        w.Tooltip("Done before, and the repeat timer is up.");
        w.SameLine();
        w.Checkbox("On timer", "lg.timer", _legHideReady, v => { _legHideReady = v; if (v) { _legMissingOnly = false; _legReadyOnly = false; } });
        w.Tooltip("Done before and still waiting on the repeat timer.");

        w.BeginChild("lg.list", 0f, 0f, border: true);
        foreach (string group in LegendaryList.Groups)
        {
            bool headed = false;
            foreach (LegendaryQuest q in LegendaryList.All)
            {
                if (q.Group != group) continue;
                TimeSpan? left = q.TimeLeft(flags, now);
                bool done = left != null;
                bool isReady = done && left!.Value <= TimeSpan.Zero;
                if (_legMissingOnly && done) continue;
                if (_legReadyOnly && !isReady) continue;
                if (_legHideReady && (!done || isReady)) continue;

                if (!headed) { w.TextColored(UiColors.Gold, group); headed = true; }
                w.TextColored(done ? UiColors.Green : UiColors.Red, done ? "have" : "need");
                w.SameLine(50f);
                LegendaryQuest row = q;
                string status = q.Status(flags, now);
                w.Selectable(Cut(q.Label, 40), "lg.q." + q.Name, false, () => PrintLegendary(row, status), 300f);
                if (q.Note.Length > 0) w.Tooltip(Cut(q.Note, 300));
                w.SameLine(360f);
                w.TextDisabled(q.Luminance > 0 ? TimeText.Number(q.Luminance) + " lum" : "");
                w.SameLine(460f);
                if (!done) w.TextColored(UiColors.Grey, "not done");
                else if (isReady) w.TextColored(UiColors.Green, "ready");
                else w.TextColored(UiColors.Yellow, status);
            }
        }
        w.EndChild();
    }

    /// <summary>Chat: the quest, its flags and status, the sheet's notes, wiki and video links.</summary>
    private void PrintLegendary(LegendaryQuest q, string status)
    {
        PrintEntry(q.Label, string.Join(", ", q.Flags), status, q.Note, q.Url);
        if (q.Video.Length > 0) _c.Print("Video: " + q.Video);
    }
}
