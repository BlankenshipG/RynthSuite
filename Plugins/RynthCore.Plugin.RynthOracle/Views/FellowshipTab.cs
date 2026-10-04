// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// Fellowship: name, leader, open/locked, experience sharing and the members (upstream's status
/// list). Read from the client's fellowship object through RynthAi's FellowshipTracker (shared
/// source). The recruiting tools are not ported (see the port report).
/// </summary>
internal sealed partial class OracleView
{
    private const int MaxFellows = 9; // retail

    private void DrawFellowship(UiWindow w)
    {
        FellowshipTracker f = _c.Fellowship;
        if (!f.IsInFellowship)
        {
            w.Text("Not in a fellowship.");
            return;
        }

        int leaderId = f.LeaderId;
        int count = f.MemberCount;
        string leaderName = "";
        for (int i = 0; i < count; i++)
            if (f.GetMemberId(i) == leaderId) leaderName = f.GetMemberName(i);

        w.SeparatorText(f.FellowshipName.Length > 0 ? f.FellowshipName : "Fellowship");
        Field(w, "Leader", leaderName.Length > 0 ? leaderName : "-", 0f, 110f);
        Field(w, "Members", $"{count} / {MaxFellows}", 300f, 400f);
        Field(w, "Open", f.IsOpen ? "yes" : "no", 0f, 110f);
        Field(w, "Locked", f.IsLocked ? "yes" : "no", 300f, 400f);
        Field(w, "Experience", f.ShareXP ? "shared" : "not shared", 0f, 110f);
        bool canRecruit = (leaderId == (int)_c.Character.PlayerId || f.IsOpen) && count < MaxFellows;
        Field(w, "Recruit", count >= MaxFellows ? "fellowship full" : canRecruit ? "you can recruit" : "must be open or leader", 300f, 400f);

        w.SeparatorText("Members");
        for (int i = 0; i < count; i++)
        {
            string name = f.GetMemberName(i);
            if (name.Length == 0) continue;
            if (f.GetMemberId(i) == leaderId) w.TextColored(UiColors.Gold, name + " (leader)");
            else w.Text(name);
        }
    }
}
