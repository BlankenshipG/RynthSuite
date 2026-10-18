using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// The rest of ub-IT's /ub verbs (2026-10-18 meta-compatibility pass). Three kinds:
///   1. Aliases and features with a RynthAi, RynthNav, RynthSort, engine or server equivalent
///      (wear, getxy, stfl/stfr, arrow, navgoto, frame limits, fellow dismiss/leader/status,
///      qblist, augs, window save, packsort*, miniremote, meta/combat/sloot on|off, debug, lps).
///   2. ub-Rythai's verbs, which RynthAi leaves to that plugin without a reply.
///   3. ub-IT features that mean nothing here (videopatch, chat max, playeroption, ...): accepted
///      without error, said once per session, so a meta that sends them keeps running.
/// </summary>
public sealed partial class RynthAiPlugin
{
    /// <summary>Verbs the ub-Rythai plugin answers. Every plugin sees each /ub line, so RynthAi stays quiet for these.</summary>
    private static readonly HashSet<string> UbRythaiVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "autopercent", "autotradeaccept", "bank", "build", "guard", "guardian", "ilt", "leaftide",
        "log", "loginq", "p2", "periodic", "xpmeter",
    };

    /// <summary>
    /// ub-IT verbs with no RynthAi equivalent: diagnostics of ub-IT's own tools, Decal UI and
    /// client patches, and features RynthCore does its own way. Accepted so metas keep running.
    /// </summary>
    private static readonly HashSet<string> UbNoEquivalentVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "acobject", "autologin", "autoloot", "autosalvage", "autosalvagedebug", "autosalvagereload",
        "bankparse", "breakallegiance", "bridge", "calcdamage", "chat", "chatfilter", "chunkaeth",
        "chunkaetheria", "chunkdebug", "chunkreset", "chunkstatus", "clapstatus", "dispel", "dispeltool",
        "egg", "element", "enlighten", "enlightenment", "getjob", "getui", "invcache", "joke", "keystore",
        "login", "lsdclearcache", "lsdlb", "mtbridge", "nav", "navfollow", "navfollowarrival",
        "onetouchheal", "pcap", "perf", "playeroption", "prepclick", "printcolors", "professor",
        "questbonus", "queuestatus", "refreshqueues", "resolution", "revalidate", "setui", "shittalk",
        "soak", "spell", "statushud", "storm", "surgingstrength", "swearallegiance", "testassessor",
        "textures", "tinkcalc", "trace", "tracearmorbuffs", "ubhudrecover", "ucm", "unstack",
        "vhudbridgestatus", "videopatch", "xp", "xpdebug",
    };

    // No-equivalent verbs already reported this session (one chat line each, not one per meta pulse).
    private readonly HashSet<string> _ubNoEquivalentSaid = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when ub-Rythai owns this /ub verb. "/ub portal use|list" (portal gems) is
    /// ub-Rythai's; "/ub portal &lt;name&gt;" (the nearest portal) is RynthAi's.</summary>
    internal static bool IsUbRythaiCommand(string cmd, string args)
    {
        if (UbRythaiVerbs.Contains(cmd)) return true;
        if (!cmd.Equals("portal", StringComparison.OrdinalIgnoreCase)) return false;
        string first = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } w ? w[0] : "";
        return first.Length == 0 || first.Equals("use", StringComparison.OrdinalIgnoreCase)
                                 || first.Equals("list", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The verb and its arguments from a "/ub verb args" line.</summary>
    private static (string Cmd, string Args) SplitUbLine(string fullCommand)
    {
        string rest = fullCommand.Length > 3 ? fullCommand.Substring(3).Trim() : "";
        int sp = rest.IndexOf(' ');
        return sp > 0 ? (rest.Substring(0, sp).ToLowerInvariant(), rest.Substring(sp + 1).Trim())
                      : (rest.ToLowerInvariant(), "");
    }

    /// <summary>
    /// A meta's chat-command line. /ub lines go to the /ub dispatcher (ub-IT meaning: "/ub give" gives
    /// every match, where "/mt give" gives one). ub-Rythai's verbs return false, so the meta hands the
    /// line to the chat parser, which every plugin's chat handler sees. Anything else is /mt.
    /// </summary>
    internal bool HandleMetaChatCommand(string cmd)
    {
        if (cmd.StartsWith("/ub ", StringComparison.OrdinalIgnoreCase))
        {
            var (verb, args) = SplitUbLine(cmd);
            return !IsUbRythaiCommand(verb, args) && HandleUbCommand(cmd);
        }
        return HandleMtCommand(cmd);
    }

    /// <summary>The verbs above. Returns false for anything else.</summary>
    internal bool TryHandleUbCompat(string cmd, string fullCommand)
    {
        string args = SplitUbLine(fullCommand).Args;
        string[] w = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string sub = w.Length > 0 ? w[0].ToLowerInvariant() : "";

        switch (cmd)
        {
            // ── Aliases of commands RynthAi already has ─────────────────────────
            case "wear":  UbWear(args, partial: false); return true;
            case "wearp": UbWear(args, partial: true); return true;
            case "getxy": return HandleMtCursor();
            case "stfl":  return HandleMtStrafe(new[] { "/mt", "strafe", "left", args });
            case "stfr":  return HandleMtStrafe(new[] { "/mt", "strafe", "right", args });
            case "debug": HandleRaCommand(("/ra debug " + args).TrimEnd()); return true;

            case "bgframelimit":
            case "globalframelimit":
                UbFrameLimit(background: cmd == "bgframelimit", args);
                return true;

            case "fellow" when sub is "dismiss" or "dismissp" or "leader" or "leaderp" or "status" or "recruitp":
                UbFellow(sub, w.Length > 1 ? string.Join(" ", w, 1, w.Length - 1) : "");
                return true;

            // ── RynthNav: the arrow and travel to coordinates ───────────────────
            case "arrow":
                if (sub == "point" && args.Length > 6)
                    SendToPlugin("RynthNav", "rnav", "arrow " + args.Substring(6).Trim(), "/ub arrow needs the RynthNav plugin.");
                else
                    UbNoEquivalent(cmd, "\"arrow face\" has no RynthNav equivalent");
                return true;
            case "navgoto":
                UbNavGoto(w);
                return true;

            // ── RynthSort: pack sorting ─────────────────────────────────────────
            case "packsort":
            case "packsortscansort":
            case "packsortscanforcesort":
                SendToPlugin("RynthSort", "rsort", "now", "/ub packsort needs the RynthSort plugin.");
                return true;
            case "packsortshow":
            case "packsortlist":
                SendToPlugin("RynthSort", "rsort", "status", "/ub packsort needs the RynthSort plugin.");
                return true;
            case "packsortreload":
            case "packsortscan":
            case "packsortscanforce":
                UbNoEquivalent(cmd, "RynthSort reads its rules itself");
                return true;

            // ── Server commands (ACECustom) ─────────────────────────────────────
            case "qblist": InvokeServerCommand("/qb list"); return true;
            case "augs":   InvokeServerCommand("/aug"); return true;

            // ── RynthCore's window placement ────────────────────────────────────
            case "window":
                if (sub == "save") InvokeServerCommand("/rc window save");
                else UbNoEquivalent(cmd + " " + sub, "RynthCore restores the saved window at login (/rc window save|reset)");
                return true;

            // ── RynthAi's Mini Remote ───────────────────────────────────────────
            case "miniremote":
                if (sub is "show" or "hide" or "toggle") HandleHudCommand("miniremote", sub);
                else UbNoEquivalent(cmd + " " + sub, "use /ub miniremote show|hide|toggle");
                return true;

            // ── Project Storm's switches onto RynthAi's macro features ──────────
            case "meta":   return UbStormSwitch(cmd, sub, "enablemeta", w);
            case "combat": return UbStormSwitch(cmd, sub, "enablecombat", w);
            case "sloot":  return UbStormSwitch(cmd, sub, "enablelooting", w);

            case "lps":
            case "lootprofilestatus":
                {
                    string path = _dashboard?.Settings?.CurrentLootPath ?? "";
                    ChatLine("[RynthAi] Loot profile: " + (path.Length > 0 ? Path.GetFileName(path) : "(none)"));
                    return true;
                }
        }

        // ── ub-IT features with no RynthAi equivalent ───────────────────────────
        if (UbNoEquivalentVerbs.Contains(cmd))
        {
            UbNoEquivalent(cmd, null);
            return true;
        }
        return false;
    }

    /// <summary>Says once per session that a /ub verb is accepted and ignored; every use is logged.</summary>
    private void UbNoEquivalent(string what, string? note)
    {
        Log($"[RynthAi] /ub {what}: no RynthAi equivalent; ignored.");
        if (_ubNoEquivalentSaid.Add(what))
            ChatLine($"[RynthAi] /ub {what} is a ub-IT feature with no RynthAi equivalent; it is accepted and ignored so metas keep running"
                     + (note != null ? $" ({note})." : "."));
    }

    /// <summary>Sends a chat line to the chat parser (server commands, /rc).</summary>
    private void InvokeServerCommand(string line)
    {
        if (Host.HasInvokeChatParser) Host.InvokeChatParser(line);
        else ChatLine($"[RynthAi] Can't send {line}: the chat parser isn't available.");
    }

    /// <summary>A command for another plugin through the engine broker.</summary>
    private void SendToPlugin(string plugin, string action, string value, string missing)
    {
        if (!(Host.HasSendPluginCommand && Host.SendPluginCommand(plugin, action, value)))
            ChatLine("[RynthAi] " + missing);
    }

    /// <summary>/ub wear[p] &lt;name&gt;: wield a carried item by name, skipping copies already worn
    /// (using a worn item would take it off).</summary>
    private void UbWear(string name, bool partial)
    {
        if (string.IsNullOrWhiteSpace(name)) { ChatLine("[RynthAi] Usage: /ub wear <item name> (wearp: partial name)"); return; }
        if (_objectCache == null || !Host.HasUseObject) { ChatLine("[RynthAi] /ub wear: not ready (log in first)."); return; }

        bool sawWorn = false;
        foreach (var wo in _objectCache.GetInventory())
        {
            bool match = partial
                ? wo.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                : string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase);
            if (!match) continue;
            if (wo.Values(LongValueKey.CurrentWieldedLocation, 0) > 0) { sawWorn = true; continue; }
            Host.UseFor(unchecked((uint)wo.Id), "Command", "/ub wear", UseKind.Asked);
            Log($"[RynthAi] /ub wear: {wo.Name} (0x{(uint)wo.Id:X8})");
            return;
        }
        ChatLine(sawWorn ? $"[RynthAi] Already wearing '{name}'." : $"[RynthAi] Item not found: '{name}'");
    }

    /// <summary>/ub globalframelimit|bgframelimit &lt;fps&gt;: RynthAi's frame limit (Settings, Display).
    /// 0 turns the limit off (global) or lifts the background cap to its maximum.</summary>
    private void UbFrameLimit(bool background, string arg)
    {
        LegacyUiSettings? s = _dashboard?.Settings;
        if (s == null) { ChatLine("[RynthAi] Settings aren't loaded yet."); return; }
        if (!int.TryParse(arg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps) || fps < 0)
        {
            ChatLine($"[RynthAi] Usage: /ub {(background ? "bgframelimit" : "globalframelimit")} <frames per second>");
            return;
        }
        if (background)
        {
            s.TargetFPSBackground = fps == 0 ? 60 : Math.Clamp(fps, 5, 60);
            s.EnableFPSLimit = true;
            ChatLine($"[RynthAi] Background frame limit: {s.TargetFPSBackground} fps.");
        }
        else if (fps == 0)
        {
            s.EnableFPSLimit = false;
            ChatLine("[RynthAi] Frame limit off.");
        }
        else
        {
            s.TargetFPSFocused = Math.Clamp(fps, 10, 240);
            s.EnableFPSLimit = true;
            ChatLine($"[RynthAi] Frame limit: {s.TargetFPSFocused} fps.");
        }
        try { _dashboard?.SaveSettings(); } catch { }
    }

    /// <summary>/ub fellow dismiss[p]|leader[p] &lt;name&gt;, status, recruitp &lt;name&gt; (the p forms match part of a name).</summary>
    private void UbFellow(string sub, string name)
    {
        if (sub == "status") { HandleFellowshipCommand(new[] { "/ra", "fellow", "status" }); return; }
        if (name.Length == 0) { ChatLine($"[RynthAi] Usage: /ub fellow {sub} <name>"); return; }

        bool partial = sub.EndsWith('p');
        string resolved = name;
        if (partial && sub != "recruitp" && _fellowshipTracker != null)
        {
            foreach (string member in _fellowshipTracker.GetMemberNames())
                if (member.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { resolved = member; break; }
        }
        else if (sub == "recruitp" && FindObject(name, inv: false, land: true, partial: true) is { } wo)
        {
            resolved = wo.Name;
        }

        string verb = sub.TrimEnd('p');
        InvokeServerCommand($"/fellowship {verb} {resolved}");
    }

    /// <summary>/ub navgoto &lt;ns&gt; &lt;ew&gt; [walk|run]: ub-IT's signed coordinates (south and west
    /// negative) to RynthNav's "/rnav go 42.1N, 33.6E". RynthNav picks its own pace.</summary>
    private void UbNavGoto(string[] w)
    {
        if (w.Length < 2
            || !double.TryParse(w[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double ns)
            || !double.TryParse(w[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ew))
        {
            ChatLine("[RynthAi] Usage: /ub navgoto <ns> <ew> [walk|run] (south and west are negative)");
            return;
        }
        string coords = FormattableString.Invariant($"{Math.Abs(ns):0.0#}{(ns < 0 ? 'S' : 'N')}, {Math.Abs(ew):0.0#}{(ew < 0 ? 'W' : 'E')}");
        SendToPlugin("RynthNav", "rnav", "go " + coords, "/ub navgoto needs the RynthNav plugin.");
    }

    /// <summary>
    /// Project Storm's /ub meta|combat|sloot: on/off switch the matching RynthAi macro feature
    /// (enablemeta, enablecombat, enablelooting); "meta load &lt;name&gt;" and "meta state &lt;name&gt;"
    /// are VTank's meta load and setmetastate. The other Storm verbs have no equivalent.
    /// </summary>
    private bool UbStormSwitch(string cmd, string sub, string vtOption, string[] w)
    {
        string rest = w.Length > 1 ? string.Join(" ", w, 1, w.Length - 1) : "";
        if (_metaManager == null) { ChatLine("[RynthAi] Not ready (log in first)."); return true; }

        switch (sub)
        {
            case "on" or "off":
                _metaManager.TryHandleVtCommand($"/vt opt set {vtOption} {(sub == "on" ? "true" : "false")}");
                return true;
            case "status" or "":
                _metaManager.TryHandleVtCommand($"/vt opt get {vtOption}");
                return true;
            case "load" when cmd == "meta" && rest.Length > 0:
                _metaManager.TryHandleVtCommand("/vt meta load " + rest);
                return true;
            case "state" when cmd == "meta" && rest.Length > 0:
                _metaManager.TryHandleVtCommand("/vt setmetastate " + rest);
                return true;
        }
        UbNoEquivalent(cmd + " " + sub, "Project Storm's own engine; RynthAi is the macro here");
        return true;
    }
}
