using System;
using System.IO;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>About: the credit to Oracle of Dereth's author, the settings and the quest list's source.</summary>
internal sealed partial class OracleView
{
    private void DrawAbout(UiWindow w)
    {
        w.TextColored(UiColors.Gold, "Based on Oracle of Dereth by Advis Eveldan - thank you!");
        w.TextDisabled("github.com/advis61/OracleOfDereth (MIT licence). The quest, augmentation, society, title and marker lists are Advis's.");
        w.Text($"RynthOracle {RynthOraclePlugin.Version}");

        OracleSettings s = _c.Settings;
        w.SeparatorText("Settings");
        w.Checkbox("Read quest flags when you log in", "o.readlogin", s.ReadQuestsAtLogin, v => { s.ReadQuestsAtLogin = v; s.Save(); });
        w.Tooltip("Sends /myquests once after login so the quest pages have data.");
        w.Checkbox("Hide the replies to RynthOracle's own requests", "o.hide", s.HideOwnQuestReplies, v => { s.HideOwnQuestReplies = v; s.Save(); });
        w.Tooltip("/myquests, and on their servers /b, /augs, /top and the like. A command you type yourself always shows its reply.");
        w.Checkbox("Show the Void window for characters with Void Magic", "o.void", s.AutoShowVoidWindow, v => { s.AutoShowVoidWindow = v; s.Save(); });
        w.Checkbox("Check once a day for a newer quest list", "o.daily", s.CheckQuestListDaily, v => { s.CheckQuestListDaily = v; s.Save(); });
        w.Tooltip("Downloads Oracle of Dereth's master quest list from GitHub, as the original plugin does.");

        w.SeparatorText("Server");
        ServerIdentity sv = _c.Server;
        w.Text(sv.Tag.Length > 0 ? $"{sv.Tag}: its own pages and quest pack are on." : "No server-specific pages here (retail features only).");
        w.TextDisabled($"How it was decided: {sv.Source}");
        if (_c.Host.Version < 75) w.TextDisabled($"This engine is API v{_c.Host.Version}; v75 adds a surer server check and the titles you hold.");

        w.SeparatorText("Quest list");
        QuestCatalog cat = _c.Catalog;
        w.Text($"{cat.RetailCount} retail quests from the {cat.Source}" +
               (cat.PackServer.Length > 0 ? $", plus {cat.ServerCount} {cat.PackServer} quests ({cat.PackSource})" : ""));
        if (cat.HiddenCount > 0) w.TextDisabled($"{cat.HiddenCount} retail quests are hidden: {cat.PackServer}'s /myquests can't show their flags.");
        if (s.LastQuestListCheckUtc != DateTime.MinValue)
            w.TextDisabled($"Last checked {s.LastQuestListCheckUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (_c.Updater.Running) w.TextColored(UiColors.Yellow, "Checking...");
        else w.Button("Check for a newer list now", "o.check", () => StartQuestListCheck());
        if (!_c.Catalog.UsingBundled)
        {
            w.SameLine();
            w.Button("Go back to the bundled list", "o.bundled", UseBundledList);
        }
        if (_c.UpdaterMessage.Length > 0) w.TextWrapped(_c.UpdaterMessage);

        w.SeparatorText("Commands");
        w.BulletText("/ro  open or close this window");
        w.BulletText("/ro void  open or close the Void window");
        w.BulletText("/ro quests  read your quest flags now");
        w.BulletText("/ro help  list the commands in chat");
        w.TextDisabled("A server quest pack can be replaced without a rebuild: put quests.<server>.csv in %APPDATA%\\RynthCore\\RynthOracle\\packs.");
    }

    private void StartQuestListCheck()
    {
        _c.Settings.LastQuestListCheckUtc = DateTime.UtcNow;
        _c.Settings.Save();
        _c.UpdaterMessage = "";
        _c.Updater.Start();
    }

    private void UseBundledList()
    {
        try { File.Delete(QuestCatalog.DownloadedPath); }
        catch (Exception ex) { _c.UpdaterMessage = "Couldn't remove the downloaded list: " + ex.Message; return; }
        _c.Catalog.Load(_c.Server.Tag, null);
        _c.Catalog.SyncDiscoveries(_c.Flags);
        _c.UpdaterMessage = "Using the bundled quest list.";
    }
}
