// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// Fellowship: name, leader, open/locked, experience sharing and the members (upstream's status
/// list). Read from the client's fellowship object through RynthAi's FellowshipTracker (shared
/// source). The size limit and, on Infinite Leaftide, the server's fellowship features (Zone Share,
/// luminance share, Fellowship Blackjack) come from RynthILT's "RynthILT.Server" profile. The
/// recruiting tools are not ported (see the port report).
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
        List<KeyValuePair<int, string>> members = f.GetMembers();
        int leaderId = f.LeaderId;
        int count = System.Math.Max(f.MemberCount, members.Count);
        string leaderName = "";
        foreach (KeyValuePair<int, string> m in members)
            if (m.Key == leaderId) leaderName = m.Value;

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
        foreach (KeyValuePair<int, string> m in members)
        {
            if (m.Value.Length == 0) continue;
            if (m.Key == leaderId) w.TextColored(UiColors.Gold, m.Value + " (leader)");
            else w.Text(m.Value);
        }

        if (ilt) DrawLeaftideFellowship(w, max);
    }

    /// <summary>
    /// Infinite Leaftide's fellowship rules as RynthILT saved them from the server check: the size
    /// limit and each fellowship feature with its state and where to use it.
    /// </summary>
    private void DrawLeaftideFellowship(UiWindow w, int max)
    {
        w.SeparatorText("Infinite Leaftide");
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
}
