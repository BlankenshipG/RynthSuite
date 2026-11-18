// RynthAiPlugin.AfterDeath.cs - Keep Hunting After Death (2026-11-13).
//
// Players asked for the hunt to stay on after a death, and the same for the patrol. Off by default:
// a death still ends the Outdoor Hunt (from the lifestone it would hunt round the lifestone). With
// Keep Hunting After Death on and the macro still running (Stop Macro On Death off):
//   - the Outdoor Hunt is paused at the death and starts again once the character is back up and
//     the death's own steps are done (the settle after the respawn, Recall On Death, Travel Back To
//     My Corpse), from where the character stands then (its Hunt Radius is round that spot);
//   - the Dungeon Patrol, which already carries on when the character gets back into its dungeon,
//     also follows it into another dungeon a death sent it to (DecidePatrolFollow).
// A route loaded meanwhile (a meta's death handling), the macro stopped, or the box turned off: the
// hunt stays off and says why. Tick thread.
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

/// <summary>The decision half of the hunt's restart after a death (pure, for the tests).</summary>
internal static class AfterDeath
{
    internal enum HuntStep
    {
        /// <summary>Not yet: the death's steps aren't done, or the character is in portal space.</summary>
        Wait,
        /// <summary>Start the hunt again from here.</summary>
        Start,
        /// <summary>The box was turned off: forget it quietly.</summary>
        DropOff,
        /// <summary>The macro was stopped: forget it and say so.</summary>
        DropMacro,
        /// <summary>Another route was loaded meanwhile (a meta's death handling): forget it and say so.</summary>
        DropRoute,
    }

    internal const string Tip =
        "After a death, the Outdoor Hunt starts again once you're back up: after Recall On Death and\n" +
        "Travel Back To My Corpse when they're on, from where you are then (Hunt Radius is round that spot).\n" +
        "The Dungeon Patrol carries on in its dungeon, and also in a dungeon a recall takes you to after the death.\n" +
        "Needs the macro to keep running (Stop Macro On Death off). A route a meta loads after the death wins.";

    internal static HuntStep DecideHunt(bool keepOn, bool macroRunning, bool routeReplaced, bool deathStepsBusy, bool portaling)
    {
        if (!keepOn) return HuntStep.DropOff;
        if (!macroRunning) return HuntStep.DropMacro;
        if (routeReplaced) return HuntStep.DropRoute;
        if (deathStepsBusy || portaling) return HuntStep.Wait;
        return HuntStep.Start;
    }
}

public sealed partial class RynthAiPlugin
{
    private bool _huntAfterDeath;                  // the hunt was on at a death, to start again once back up
    private NavRouteParser? _huntAfterDeathRoute;   // the empty route the paused hunt left (another one = a meta took over)

    internal const string KeepHuntingUsage = "/ra hunt death [on|off]";

    /// <summary>The death edge (HoldForDeath): the hunt ends, or with Keep Hunting After Death pauses until the character is back up.</summary>
    private void PauseHuntForDeath()
    {
        var s = _dashboard?.Settings;
        bool keep = (_hunt.Active || _huntAfterDeath) && s is { KeepHuntingAfterDeath: true, IsMacroRunning: true };
        if (_hunt.Active)
            StopHunt(keep ? "died; it starts again once you're back up (Keep Hunting After Death)" : "died", say: true);
        _huntAfterDeath = keep;
        _huntAfterDeathRoute = keep ? s!.CurrentRoute : null;
    }

    /// <summary>Each tick the hunt is off (TickOutdoorHunt): the paused hunt starts again when the death's steps are done.</summary>
    private void TickHuntAfterDeath()
    {
        if (!_huntAfterDeath) return;
        var s = _dashboard?.Settings;
        if (s == null) return;
        bool replaced = !ReferenceEquals(s.CurrentRoute, _huntAfterDeathRoute) || s.CurrentNavPath.Length > 0;
        bool busy = _deathHolding || _deathRecall != null || _ownCorpseTravel != OwnCorpseTravelState.None;
        bool portaling = Host.HasIsPortaling && Host.IsPortaling();
        var step = AfterDeath.DecideHunt(s.KeepHuntingAfterDeath, s.IsMacroRunning, replaced, busy, portaling);
        if (step == AfterDeath.HuntStep.Wait) return;
        _huntAfterDeath = false;
        _huntAfterDeathRoute = null;
        switch (step)
        {
            case AfterDeath.HuntStep.Start:
                StartHunt(afterDeath: true);
                break;
            case AfterDeath.HuntStep.DropMacro:
                ChatLine(KeepHuntingRefusedPrefix + "the macro is off.");
                break;
            case AfterDeath.HuntStep.DropRoute:
                ChatLine(KeepHuntingRefusedPrefix + $"{PatrolReplacementName(s)} is the route now.");
                break;
            default:
                Host.Log("[RynthAi] Hunt: not started again after the death (Keep Hunting After Death was turned off).");
                break;
        }
    }

    internal const string KeepHuntingRefusedPrefix = "[RynthAi] Keep Hunting After Death: the hunt didn't start again: ";

    /// <summary>"/ra hunt death [on|off]": Keep Hunting After Death. No word: says it now.</summary>
    private void SetKeepHuntingAfterDeath(string value)
    {
        var s = _dashboard?.Settings;
        if (s == null) { ReplyFail("[RynthAi] Settings not ready."); return; }
        string v = value.Trim().ToLowerInvariant();
        if (v.Length == 0)
        {
            ReplyOk($"[RynthAi] Keep Hunting After Death: {(s.KeepHuntingAfterDeath ? "on" : "off")}. {KeepHuntingUsage} to change it.");
            return;
        }
        bool? on = v switch
        {
            "on" or "true" or "1" or "yes" => true,
            "off" or "false" or "0" or "no" => false,
            _ => null,
        };
        if (on is not bool want)
        {
            ReplyFail($"[RynthAi] Keep Hunting After Death: '{value.Trim()}' isn't on or off (it is {(s.KeepHuntingAfterDeath ? "on" : "off")} now). Usage: {KeepHuntingUsage}");
            return;
        }
        s.KeepHuntingAfterDeath = want;
        ReplyOk(want
            ? "[RynthAi] Keep Hunting After Death set: on. After a death the hunt starts again once you're back up (after Recall On Death and Travel Back To My Corpse), and the Dungeon Patrol carries on."
            : "[RynthAi] Keep Hunting After Death set: off. A death ends the hunt.");
    }
}
