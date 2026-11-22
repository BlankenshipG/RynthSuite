using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RynthCore.Plugin.RynthOracle.Data;

// How to get the Leaftide wiki login the ILT quest guide needs, on the ILT Tracker tab (2026-11-21, Silentkelpie: "add in
// the itl wiki how to to this tab as well"). The same steps as RynthILT's WikiLoginGuide (RynthServers,
// IltWiki/WikiLoginGuide.cs, shown in /ilt settings > Quest guide and by "/ilt wiki help"): every page of
// leaftidewiki.com needs a login, so RynthILT reads it with a bot password, a read-only second password of the player's
// own wiki account. RynthOracle never sees or keeps the login: the last step hands over to RynthILT's settings. The pages
// open in the system browser (ShellExecuteW on its own short STA thread, never Process.Start inside acclient); only the
// fixed addresses below are ever opened.
internal static class IltWikiGuide
{
    public const string CreateAccountUrl = "https://www.leaftidewiki.com/index.php?title=Special:CreateAccount";
    public const string LoginUrl = "https://www.leaftidewiki.com/index.php?title=Special:UserLogin";
    public const string BotPasswordsUrl = "https://www.leaftidewiki.com/index.php?title=Special:BotPasswords";
    public const string SuggestedBotName = "rynth";

    /// <summary>One step: a heading, what to do, and the page it happens on ("" none).</summary>
    public readonly record struct Step(string Title, string Text, string Url);

    public static readonly Step[] Steps =
    {
        new("Make a wiki account",
            "Open the wiki's Create account page. Pick a user name and a password, and answer the Asheron's Call question it "
            + "asks (it keeps bots out). The account works at once; if the wiki turns it down, ask on the Infinite Leaftide "
            + "Discord (the wiki's home page links it). Already have one? Skip to step 2.",
            CreateAccountUrl),
        new("Log in on the wiki",
            "Log in with that user name and password, in your browser. RynthILT and RynthOracle never see this password.",
            LoginUrl),
        new("Make a bot password",
            "Open Special:BotPasswords. Type a bot name (for example \"" + SuggestedBotName + "\") and press Create. Tick only "
            + "\"Basic rights\" (reading is all RynthILT does), leave the rest, and press Create again.",
            BotPasswordsUrl),
        new("Copy the login it shows",
            "The wiki shows the new login once: the login name, YourName@" + SuggestedBotName + ", and a long password. Copy "
            + "both now; the password can't be shown again (make a new one if you lose it).",
            ""),
        new("Type it into RynthILT",
            "Press RynthILT settings below. Under Quest guide, type the login name (YourName@" + SuggestedBotName + ") and the "
            + "bot password, then Save login and Read the wiki now. The password stays on this PC, sealed for your Windows "
            + "user; /ilt wiki logout forgets it.",
            ""),
        new("Done",
            "RynthILT reads the wiki's quest pages, Dynamic Stamps and Kill Tasks; RynthOracle's Wiki, Kill Tasks and Stamps "
            + "tabs show them. It reads them again at login once a day (/ilt wiki for the status).",
            ""),
    };

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ShellExecuteW")]
    private static extern IntPtr ShellExecuteW(IntPtr hwnd, string? operation, string file, string? parameters, string? directory, int showCmd);

    /// <summary>The guide's own pages only (nothing typed or read elsewhere is ever opened).</summary>
    internal static bool IsGuidePage(string url) => url is CreateAccountUrl or LoginUrl or BotPasswordsUrl;

    /// <summary>Opens one of the guide's pages in the system browser, off the render thread. False for any other address.</summary>
    public static bool Open(string url)
    {
        if (!IsGuidePage(url)) return false;
        try
        {
            var t = new Thread(() =>
            {
                try { ShellExecuteW(IntPtr.Zero, "open", url, null, null, 1); } catch { }
            }) { IsBackground = true, Name = "RynthOracle wiki page" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return true;
        }
        catch { return false; }
    }
}
