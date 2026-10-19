// ============================================================================
//  RynthSuite - Combat/BuffManager.HpFailsafe.cs
//  The HP failsafe: at or under Emergency Heal At, a character whose heals keep
//  failing (refused, fizzled, never sent, or out-healed) must not sit and die.
//
//  Measured by health, not by counting heal attempts: a round of FailsafeRoundMs
//  with no rise of FailsafeRiseMinPct counts as a failed round (whatever the
//  reason: a stuck cast, 'too busy', no mana, no kits).
//    1. FailsafeRoundsToBreak failed rounds: ActionPacer.ForceBreak (a jump; on
//       ACE's fast tick it fails the cast in progress, Player.HandleActionJump).
//       Starts are held until landed, then the vitals step heals again.
//    2. FailsafeRoundsToRecall more failed rounds after the break: recall to the
//       lifestone ("/lifestone", ACE GameAction TeleToLifestone) when
//       HpFailsafeRecall is on. The macro is switched off first, because ACE
//       cancels the recall when the character moves during its animation and
//       refuses it while busy (Player_Location.HandleActionTeleToLifestone).
//    3. No "<our name> is recalling to the lifestone." after RecallMaxSends tries
//       (ACE refuses the recall while busy, under a PK timer or with recalls
//       disabled, which is just the stuck state this is for): the macro is
//       switched back on, so the heals and kits run again, and the failsafe
//       re-arms; the next recall waits RecallCooldownMs. A failed recall must not
//       leave the character standing at low health with nothing running. Only a
//       macro the failsafe itself stopped is switched back on, never past a VTank
//       yield (MayRestartMacro), and a death drops the pending recall (the player's
//       Stop Macro On Death stands).
//  Health back over the line (or 100%) ends the episode. Nothing counts in portal space.
//  The failsafe (jump included) is for a character that can heal: it arms only with a heal that went
//  out in the last minute, or a heal spell / kit / health potion at hand (HealCapableNow). A melee
//  character with nothing to heal with is not jumped or recalled. HpFailsafeRecall defaults to off.
// ============================================================================

using System;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

public partial class BuffManager
{
    internal const double FailsafeRoundMs = 2500;
    internal const int FailsafeRoundsToBreak = 3;
    internal const int FailsafeRoundsToRecall = 3;
    // A rise this large (health %) means a heal landed; natural regeneration is smaller.
    private const int FailsafeRiseMinPct = 3;
    // The recall goes this long after the macro stop (the stop edge runs on the next tick),
    // and is sent again this often, a few times, until our "is recalling to the lifestone" is seen.
    private const double RecallFirstDelayMs = 600;
    private const double RecallResendMs = 1500;
    private const int RecallMaxSends = 3;
    /// <summary>After a recall that never started, the next one waits this long (the heals run meanwhile).</summary>
    internal const double RecallCooldownMs = 30_000;
    internal const string RecallCommand = "/lifestone";

    private bool _fsActive;
    private DateTime _fsRoundStart;
    private int _fsLowHp;
    private int _fsFailedRounds;
    private bool _fsBroken;
    private bool _fsRecallLogged;
    private DateTime _fsRecallNotBefore = DateTime.MinValue;

    private bool _recallPending;
    private bool _recallSeen;
    private int _recallSends;
    private DateTime _recallNextAt;

    /// <summary>Set by the plugin: the rest of a /ra stop (nav stop, pause cancel) for the recall.</summary>
    internal Action? MacroStopForRecall { get; set; }
    /// <summary>Set by the plugin: false when the macro must not be started now (VTank yield). Null = may.</summary>
    internal Func<bool>? MayRestartMacro { get; set; }

    // The macro is off because the failsafe stopped it for a recall (only then does it switch it back on).
    private bool _macroStoppedByFailsafe;
    private bool _recallNameLogged;

    internal int HpFailsafeBreaks { get; private set; }
    internal int HpFailsafeRecalls { get; private set; }
    internal int HpFailsafeRecallsFailed { get; private set; }

    private bool Portaling()
    {
        try { return _host.HasIsPortaling && _host.IsPortaling(); } catch { return false; }
    }

    /// <summary>Once per heartbeat while the macro runs, before the pacing gate.</summary>
    private void TickHpFailsafe()
    {
        int at = _settings.EmergencyHealAt;
        if (at <= 0 || _vitals.MaxHealth <= 0 || _recallPending || Portaling()) { ResetHpFailsafe(); return; }
        int hp = _vitals.HealthPct;
        DateTime now = Now;
        if (hp > at || hp >= 100)
        {
            if (_fsActive && (_fsFailedRounds > 0 || _fsBroken))
                _host.Log($"[Vitals] HP failsafe: health back to {hp}% (over Emergency Heal At {at}%).");
            ResetHpFailsafe();
            return;
        }
        // Only a character that can heal at all: a melee character with no spell, kit or potion
        // (resting, or out-healed) has nothing for the failsafe to retry, and a recall there is wrong.
        if (!HealCapableNow(now)) { ResetHpFailsafe(); return; }
        if (!_fsActive)
        {
            _fsActive = true;
            _fsRoundStart = now;
            _fsLowHp = hp;
            _fsFailedRounds = 0;
            _fsBroken = false;
            return;
        }
        if (hp >= _fsLowHp + FailsafeRiseMinPct)
        {
            // A heal landed: the heals work, start counting again from here.
            _fsLowHp = hp;
            _fsRoundStart = now;
            _fsFailedRounds = 0;
            return;
        }
        if (hp < _fsLowHp) _fsLowHp = hp;
        if ((now - _fsRoundStart).TotalMilliseconds < FailsafeRoundMs) return;

        _fsFailedRounds++;
        _fsRoundStart = now;
        double failedSec = _fsFailedRounds * FailsafeRoundMs / 1000.0;
        if (!_fsBroken)
        {
            if (_fsFailedRounds < FailsafeRoundsToBreak) return;
            string why = $"HP failsafe: health {hp}% at or under Emergency Heal At {at}%, no heal landed in {failedSec:0.#} s";
            bool jumped = ActionPacer.ForceBreak(_host, why);
            if (jumped) HpFailsafeBreaks++;
            _host.Log($"[Vitals] {why}: {(jumped ? "breaking the cast with a jump, then healing again" : "no jump available (or one under way), healing again")}.");
            _fsBroken = true;
            _fsFailedRounds = 0;
            return;
        }
        if (_fsFailedRounds < FailsafeRoundsToRecall) return;
        if (!_settings.HpFailsafeRecall)
        {
            if (!_fsRecallLogged)
            {
                _fsRecallLogged = true;
                _host.Log($"[Vitals] HP failsafe: health {hp}%, heals still failing {failedSec:0.#} s after the break; HP Failsafe Recall is off, no recall.");
            }
            return;
        }
        if (now < _fsRecallNotBefore)
        {
            // The last recall never started: heal on and break again, the next recall after the cooldown.
            _fsBroken = false;
            _fsFailedRounds = 0;
            return;
        }
        StartFailsafeRecall(now, hp, failedSec);
    }

    // A heal (spell, kit or potion) that actually went out: the failsafe's proof the character can heal.
    private DateTime _lastHealSentAt = DateTime.MinValue;
    private DateTime _healCapableCheckAt = DateTime.MinValue;
    private bool _healCapableCached;
    /// <summary>A heal sent this recently proves the character can heal (kit used up since, a script's drain).</summary>
    internal const double HealSentProofMs = 60_000;

    /// <summary>Every heal that goes out (cast, kit, potion) records itself here, then feeds the latency tracker.</summary>
    private void NoteHealSent(string action)
    {
        _lastHealSentAt = Now;
        _healLatency.HealSent(Now, action);
    }

    /// <summary>
    /// Can this character heal at all? A heal went out in the last HealSentProofMs, or one could now: a known
    /// Heal Self / Stamina to Health Self with Life Magic trained, a healing kit, or a health potion
    /// (Use Potions on and one listed or in the pack). Read at most once a second (it walks the pack).
    /// </summary>
    private bool HealCapableNow(DateTime now)
    {
        if (_lastHealSentAt != DateTime.MinValue && (now - _lastHealSentAt).TotalMilliseconds < HealSentProofMs) return true;
        if ((now - _healCapableCheckAt).TotalMilliseconds < 1000 && _healCapableCheckAt != DateTime.MinValue) return _healCapableCached;
        _healCapableCheckAt = now;
        bool capable = false;
        try
        {
            if (IsSkillUsable(AcSkillType.LifeMagic))
            {
                foreach (string spell in new[] { "Heal Self", "Stamina to Health Self" })
                {
                    if (_spellManager.GetDynamicSelfBuffId(spell, AcSkillType.LifeMagic) != 0
                        || _spellManager.GetSelfSpellIdAboveAllowedTier(spell, AcSkillType.LifeMagic) != 0) { capable = true; break; }
                }
            }
            if (!capable && _worldObjectCache != null)
            {
                if (BestPackHealingKit() != null) capable = true;
                else
                {
                    foreach (var r in _settings.ConsumableRules)
                        if (r.Type.Equals("HealthKit", StringComparison.OrdinalIgnoreCase)
                            && !(r.Name ?? "").Contains("Stamina Kit", StringComparison.OrdinalIgnoreCase)
                            && ConsumableLookup.Resolve(_worldObjectCache, r) != null) { capable = true; break; }
                }
                if (!capable && _settings.UsePotions && HasListedPotion("HealthPotion")) capable = true;
            }
        }
        catch { capable = false; }
        _healCapableCached = capable;
        return capable;
    }

    private void ResetHpFailsafe()
    {
        _fsActive = false;
        _fsFailedRounds = 0;
        _fsBroken = false;
        _fsRecallLogged = false;
    }

    private void StartFailsafeRecall(DateTime now, int hp, double failedSec)
    {
        HpFailsafeRecalls++;
        _settings.IsMacroRunning = false;
        _macroStoppedByFailsafe = true;
        _recallNameLogged = false;
        MacroStopForRecall?.Invoke();
        _recallPending = true;
        _recallSeen = false;
        _recallSends = 0;
        _recallNextAt = now.AddMilliseconds(RecallFirstDelayMs);
        string line = $"HP failsafe: health {hp}%, heals still failing {failedSec:0.#} s after the break: macro stopped, recalling to the lifestone.";
        _host.Log("[Vitals] " + line);
        _host.WriteToChat("[RynthAi] " + line, 2);
        ResetHpFailsafe();
    }

    /// <summary>Every heartbeat, macro running or not: sends a pending recall, or gives up on it.</summary>
    private void TickFailsafeRecall()
    {
        if (!_recallPending) return;
        DateTime now = Now;
        if (_recallSeen)
        {
            _recallPending = false;
            _macroStoppedByFailsafe = false;
            _host.Log("[Vitals] HP failsafe: the recall started.");
            return;
        }
        if (_vitals.MaxHealth > 0 && _vitals.CurrentHealth == 0)
        {
            CancelPendingRecall("dead");
            return;
        }
        if (_settings.IsMacroRunning)
        {
            // Someone switched the macro back on: their call, the recall is dropped.
            _recallPending = false;
            _macroStoppedByFailsafe = false;
            _host.Log("[Vitals] HP failsafe: the macro was started again; the recall is dropped.");
            return;
        }
        if (now < _recallNextAt) return;
        if (_recallSends >= RecallMaxSends || !_host.HasInvokeChatParser)
        {
            _recallPending = false;
            HpFailsafeRecallsFailed++;
            _fsRecallNotBefore = now.AddMilliseconds(RecallCooldownMs);
            string why = _host.HasInvokeChatParser
                ? $"no recall seen after {_recallSends} tries of {RecallCommand} (refused: busy, a PK timer or recalls disabled)"
                : "this engine has no chat parser, so no recall";
            string what;
            if (!_macroStoppedByFailsafe) what = "the macro stays as it is";
            else if (MayRestartMacro != null && !MayRestartMacro()) what = "the macro stays off (VTank yield)";
            else
            {
                _settings.IsMacroRunning = true;   // the heals and kits run again; the failsafe re-arms
                what = "macro back on to keep healing";
            }
            _macroStoppedByFailsafe = false;
            string line = $"HP failsafe: {why}: {what}; next recall in {RecallCooldownMs / 1000:0} s at the earliest.";
            _host.Log("[Vitals] " + line);
            _host.WriteToChat("[RynthAi] " + line, 2);
            return;
        }
        _host.InvokeChatParser(RecallCommand);
        _recallSends++;
        _recallNextAt = now.AddMilliseconds(RecallResendMs);
    }

    /// <summary>
    /// The plugin's death hold (and a 0-health read): a pending recall is dropped and the macro is
    /// left off, so nothing re-sends /lifestone or restarts the macro after the respawn.
    /// </summary>
    /// <summary>
    /// A death or a portal dropped whatever the server had open: a wand use in flight is forgotten,
    /// not judged a failed swap (see _directSwapMisses). RynthAiPlugin calls it with the death hold
    /// and on entering portal space.
    /// </summary>
    internal void ForgetPendingWield(string why)
    {
        if (_pendingWieldId == 0) return;
        _pendingWieldId = 0;
        _host.Log($"[WieldGate] the wand's use in flight was cut short ({why}): not counted as a failed swap");
    }

    internal void CancelPendingRecall(string why)
    {
        if (!_recallPending) return;
        _recallPending = false;
        _macroStoppedByFailsafe = false;
        _host.Log($"[Vitals] HP failsafe: the pending recall is dropped ({why}); the macro stays off.");
    }

    /// <summary>OnChatWindowText: "<our name> is recalling to the lifestone." while a recall is pending.</summary>
    private void ObserveRecallLine(string lower)
    {
        if (!_recallPending || !lower.Contains("is recalling to the lifestone")) return;
        string me = "";
        try
        {
            uint id = unchecked((uint)_host.GetPlayerId());
            if (id != 0 && _host.HasGetObjectName && _host.TryGetObjectName(id, out string n)) me = n ?? "";
        }
        catch { me = ""; }
        // Someone else's recall ("Bob is recalling to the lifestone.") is not ours. The chat layer
        // can drop a line's first letter ("ou cast ..."), so the name may arrive without it.
        if (me.Length > 0)
        {
            string who = lower.Substring(0, lower.IndexOf(" is recalling to the lifestone", StringComparison.Ordinal)).Trim();
            string mine = me.ToLowerInvariant();
            bool ours = who == mine || (who.Length == mine.Length - 1 && who.Length > 0 && mine.EndsWith(who, StringComparison.Ordinal));
            if (!ours) return;
        }
        else if (!_recallNameLogged)
        {
            _recallNameLogged = true;
            _host.Log("[Vitals] HP failsafe: our name could not be read; any 'is recalling to the lifestone' line is taken as ours.");
        }
        _recallSeen = true;
    }
}
