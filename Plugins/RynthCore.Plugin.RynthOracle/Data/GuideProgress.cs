using System;

namespace RynthCore.Plugin.RynthOracle.Data;

// Where a guide quest stands, in four states (2026-11-22, Silentkelpie: "on all the quest, society, contracts tabs it
// should flag 4 states, quest fully done (no extra flags still needed), quest partially done (stamps to still get) redo
// timer not up, quest partially done (stamps to still get) timer up, or quest not done (no stamps done)"), and whether a
// kill task was done before ("the kill task tab should flag if the kill task has been done before or if it is the 1st
// time"). On every server, for every guide (the server's wiki, the AC Community Wiki, a server plugin's).
// A stamp counts as held when the character has it in /myquests (any run), the account's /qb list names it, or it was
// stamped this session. The timer is the redo timer of the stamps held: the longest wait left on any of them.

internal enum GuideProgressState
{
    /// <summary>No stamp of the quest held.</summary>
    NotDone,
    /// <summary>Some stamps held, some still to get, and the held ones' redo timer is still running.</summary>
    PartialWaiting,
    /// <summary>Some stamps held, some still to get, and the held ones' redo timer is up (or there is none).</summary>
    PartialReady,
    /// <summary>Every stamp held: no flag still needed.</summary>
    Done,
}

internal static class GuideProgress
{
    /// <summary>The state from the stamps held of all and the held stamps' redo wait.</summary>
    public static GuideProgressState Of(int held, int all, TimeSpan wait)
    {
        if (all <= 0 || held <= 0) return GuideProgressState.NotDone;
        if (held >= all) return GuideProgressState.Done;
        return wait > TimeSpan.Zero ? GuideProgressState.PartialWaiting : GuideProgressState.PartialReady;
    }

    /// <summary>
    /// The list's short status (it has about 14 letters of room) and the tooltip's sentence. <paramref name="repeats"/>:
    /// the quest can be done again (a timer on the wiki or the server).
    /// </summary>
    public static (string Status, string Tip) Text(GuideProgressState s, int held, int all, TimeSpan wait, bool repeats)
    {
        switch (s)
        {
            case GuideProgressState.Done:
                if (wait > TimeSpan.Zero)
                    return ("Done " + Short(wait), $"Fully done: all {all} stamps held. It can be done again in {Short(wait)}.");
                return repeats
                    ? ("Done, redo", $"Fully done: all {all} stamps held. Its timer is up: it can be done again now.")
                    : ("Done", all > 0 ? $"Fully done: all {all} stamps held, nothing still needed." : "Fully done.");
            case GuideProgressState.PartialWaiting:
                return ($"{held}/{all} wait {Short(wait)}",
                        $"Partly done: {held} of {all} stamps held, {all - held} still to get. The redo timer isn't up: {Short(wait)} left.");
            case GuideProgressState.PartialReady:
                return ($"{held}/{all} ready",
                        $"Partly done: {held} of {all} stamps held, {all - held} still to get. The redo timer is up: go get them.");
            default:
                return ("Not done", all > 0 ? $"Not done: none of its {all} stamps held yet." : "Not done.");
        }
    }

    /// <summary>"3d 4h", "5h", "9m" (short: the list's status column is narrow).</summary>
    public static string Short(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d{(t.Hours > 0 ? " " + t.Hours + "h" : "")}";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{(t.Minutes > 0 ? " " + t.Minutes + "m" : "")}";
        return $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))}m";
    }

    /// <summary>The kill task tag: done before ("Again") or the first time ("1st").</summary>
    public static (string Tag, string Tip) KillTag(bool doneBefore) => doneBefore
        ? ("Again", "Done before: a stamp or flag of this kill task is held (this character's /myquests, or the account's /qb list).")
        : ("1st", "First time: no stamp or flag of this kill task held yet.");
}
