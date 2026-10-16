// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthILT.IltFellowship;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// Fellowship: name, leader, open/locked, experience sharing and the members (upstream's status
/// list). Read from the client's fellowship object through RynthAi's FellowshipTracker (shared
/// source). The size limit and, on Infinite Leaftide, the server's fellowship features (Zone Share,
/// luminance share, Fellowship Blackjack) come from RynthILT's "RynthILT.Server" profile. On ILT
/// the server's /fship commands are buttons here too (create, invite, dismiss, invite landblock,
/// list nearby, leave, disband, help). The upstream recruiting tools are not ported (see the port report).
/// </summary>
internal sealed partial class OracleView
{
    /// <summary>
    /// ACECustom's Fellowship.MaxFellows: what an Infinite Leaftide fellowship holds when Oracle
    /// recognises the server by name but RynthILT isn't loaded to say so.
    /// </summary>
    private const int IltFellowshipFallbackMax = 29;

    /// <summary>The server's fellowship size: RynthILT's word, else 29 on ILT, else retail 9.</summary>
    private int FellowshipMax()
    {
        IltServerProfile p = _c.Ilt.Profile;
        if (p.Available && p.Ilt) return p.FellowshipMax;
        return _c.Server.Is(ServerTags.InfiniteLeaftide) ? IltFellowshipFallbackMax : RynthIltServer.RetailFellowshipMax;
    }

    private void DrawFellowship(UiWindow w)
    {
        FellowshipTracker f = _c.Fellowship;
        bool ilt = _c.Server.Is(ServerTags.InfiniteLeaftide) || (_c.Ilt.Profile.Available && _c.Ilt.Profile.Ilt);
        int max = FellowshipMax();

        if (!f.IsInFellowship)
        {
            w.Text("Not in a fellowship.");
            if (ilt) DrawLeaftideFellowship(w, max);
            return;
        }

        // One snapshot for every row: the index getters could each see a different refresh.
        List<(int Id, string Name, int Level)> members = f.GetMembers();
        int leaderId = f.LeaderId;
        int count = System.Math.Max(f.MemberCount, members.Count);
        string leaderName = "";
        foreach ((int id, string name, int _) in members)
            if (id == leaderId) leaderName = name;

        // A server that allows more than retail's nine: no limit is shown (its server plugin knows it).
        bool overRetail = count > MaxFellows;
        w.SeparatorText(f.FellowshipName.Length > 0 ? f.FellowshipName : "Fellowship");
        Field(w, "Leader", leaderName.Length > 0 ? leaderName : "-", 0f, 110f);
        Field(w, "Members", $"{count} / {max}", 300f, 400f);
        Field(w, "Open", f.IsOpen ? "yes" : "no", 0f, 110f);
        Field(w, "Locked", f.IsLocked ? "yes" : "no", 300f, 400f);
        Field(w, "Experience", f.ShareXP ? "shared" : "not shared", 0f, 110f);
        bool canRecruit = (leaderId == (int)_c.Character.PlayerId || f.IsOpen) && count < max;
        Field(w, "Recruit", count >= max ? "fellowship full" : canRecruit ? "you can recruit" : "must be open or leader", 300f, 400f);

        w.SeparatorText("Members");
        if (members.Count == 0) w.TextDisabled("Reading the member list...");
        // Leader first, then everyone else by name; the level (0 = not read yet) in its own column.
        members.Sort((a, b) => a.Id == leaderId ? -1 : b.Id == leaderId ? 1
            : string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase));
        foreach ((int id, string name, int level) in members)
        {
            if (name.Length == 0) continue;
            if (id == leaderId) w.TextColored(UiColors.Gold, name + " (leader)");
            else w.Text(name);
            if (level > 0)
            {
                w.SameLine(260f);
                w.TextDisabled("level " + level);
            }
        }

        if (ilt) DrawLeaftideFellowship(w, max);
    }

    /// <summary>
    /// Infinite Leaftide's fellowship rules as RynthILT saved them from the server check (the size
    /// limit and each fellowship feature with its state and where to use it), then the server's
    /// /fship commands as buttons.
    /// </summary>
    private void DrawLeaftideFellowship(UiWindow w, int max)
    {
        w.SeparatorText("Infinite Leaftide");
        DrawLeaftideFeatures(w, max);
        DrawFshipCommands(w);
    }

    private void DrawLeaftideFeatures(UiWindow w, int max)
    {
        IltServerProfile p = _c.Ilt.Profile;
        if (!p.Available)
        {
            w.TextWrapped($"Fellowships hold up to {max} here. Turn on RynthILT to see the server's fellowship features (Zone Share, luminance share, Fellowship Blackjack).");
            return;
        }

        Field(w, "Size limit", $"{max} members", 0f, 110f);
        Field(w, "Checked", p.CheckedUtc == System.DateTime.MinValue ? "not yet" : TimeText.Friendly(_c.UtcNow - p.CheckedUtc) + " ago", 300f, 400f);
        bool any = false;
        foreach (IltFeature feature in p.InGroup("Fellowship"))
        {
            any = true;
            w.TextDisabled(feature.Label);
            w.SameLine(180f);
            w.TextColored(feature.State switch
            {
                RynthIltServer.StateOn => UiColors.Green,
                RynthIltServer.StateOff => UiColors.Red,
                _ => UiColors.Grey,
            }, IltServerProfile.StateText(feature.State));
            if (feature.Where.Length > 0)
            {
                w.SameLine(260f);
                w.TextDisabled(feature.Where);
            }
        }
        if (!any) w.TextDisabled("RynthILT hasn't reported fellowship features yet.");
        if (p.Forced) w.TextDisabled("(This character has 'Treat this world as ILT' on in RynthILT.)");
    }

    // ── ILT /fship commands (ACECustom; the list is RynthILT's IltFellowshipCommands) ──────────

    /// <summary>A command flagged to confirm runs on a second click within this window.</summary>
    private const long FshipConfirmMs = 4000;
    /// <summary>The server answers "/fship list" once every 10 seconds; earlier clicks aren't sent.</summary>
    private const long FshipListCooldownMs = 10_000;
    private const string FshipListCommand = "/fship list";
    /// <summary>Buttons per row (the tab is about 420 px wide).</summary>
    private const int FshipButtonsPerRow = 4;

    private string _fshipName = "";
    private string _fshipArmed = "";       // the command waiting for its second click
    private long _fshipArmedUntil;
    private long _fshipListNextAt;
    private string _fshipStatus = "";

    /// <summary>
    /// The /fship buttons: a name box (a player or fellowship name; blank = the selected player for
    /// Invite / Dismiss), then one button per command. Names with spaces are sent quoted
    /// (ServerCommandShare.Compose); Leave, Disband, Dismiss and Invite landblock ask first.
    /// </summary>
    private void DrawFshipCommands(UiWindow w)
    {
        w.SeparatorText("/fship commands");
        w.InputText("Name", "fs.name", _fshipName, ServerCommandShare.MaxArgument, s => _fshipName = s);
        w.Tooltip("A player name (Invite, Dismiss) or a fellowship name (Create). Blank = your selected player.");

        long now = _c.NowMs;
        if (_fshipArmed.Length > 0 && now > _fshipArmedUntil) _fshipArmed = "";
        IReadOnlyList<ServerCommand> commands = IltFellowshipCommands.Commands;
        for (int i = 0; i < commands.Count; i++)
        {
            ServerCommand c = commands[i];
            bool armed = c.Confirm && _fshipArmed == c.Command;
            string? line = ServerCommandShare.Compose(c, _fshipName);
            if (i % FshipButtonsPerRow != 0) w.SameLine();
            w.Button(armed ? "Confirm " + c.Label + "?" : c.Label, "fs.cmd." + i, () => RunFship(c), small: true);
            w.Tooltip(c.Tooltip + "\n\n" + (line ?? "Type a name first."));
        }
        if (_fshipStatus.Length > 0) w.TextDisabled(_fshipStatus);
    }

    /// <summary>One /fship button click (plugin pump: script window events run there).</summary>
    private void RunFship(ServerCommand c)
    {
        long now = _c.NowMs;
        string? line = ServerCommandShare.Compose(c, _fshipName);
        if (line == null)
        {
            _fshipStatus = c.Label + ": type a name first.";
            return;
        }
        if (c.Confirm && !(_fshipArmed == c.Command && now <= _fshipArmedUntil))
        {
            _fshipArmed = c.Command;
            _fshipArmedUntil = now + FshipConfirmMs;
            _fshipStatus = $"Click 'Confirm {c.Label}?' again within {FshipConfirmMs / 1000} seconds to send: {line}";
            return;
        }
        _fshipArmed = "";
        if (c.Command == FshipListCommand)
        {
            if (now < _fshipListNextAt)
            {
                _fshipStatus = $"List nearby: wait {(_fshipListNextAt - now + 999) / 1000}s (the server answers once every 10 seconds).";
                return;
            }
            _fshipListNextAt = now + FshipListCooldownMs;
        }
        _fshipStatus = _c.Send(line) ? "Sent: " + line : "Couldn't send (log in first): " + line;
    }
}
