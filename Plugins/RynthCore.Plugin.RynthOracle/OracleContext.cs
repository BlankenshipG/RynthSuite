using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthOracle;

/// <summary>Everything the views read and the actions they can take. Pump thread only.</summary>
internal sealed class OracleContext
{
    public RynthCoreHost Host;
    public readonly OracleSettings Settings = new();
    public readonly QuestFlagStore Flags = new();
    public readonly QuestCatalog Catalog = new();
    public readonly QuestTimerWatcher Timers = new();
    public readonly QuestCatalogUpdater Updater = new();
    /// <summary>This character's tracked quests and recorded rewards (loaded once the name is known).</summary>
    public readonly QuestBook Book = new();
    public readonly CharacterSnapshot Character = new();
    public readonly VoidTracker Void = new();
    public readonly FellowshipTracker Fellowship = new();
    public readonly ServerIdentity Server = new();
    /// <summary>RynthILT's profile with the /qb lists (Infinite Leaftide only: polled while Server.IsIlt).</summary>
    public readonly IltServerLink Ilt = new();
    /// <summary>The Leaftide wiki quest guide RynthILT keeps (Infinite Leaftide only: polled while Server.IsIlt).</summary>
    public readonly QuestGuideClient Guide = new();

    /// <summary>
    /// RynthOracle's own quest guide from the AC Community Wiki (asheron.fandom.com), on every server (2026-11-21).
    /// Settable for the tests (a file of their own).
    /// </summary>
    public FandomFeed Fandom = new();

    /// <summary>
    /// The wiki guide in use: on Infinite Leaftide RynthILT's (its own wiki) once it has quests, else the AC
    /// Community Wiki's.
    /// </summary>
    public QuestGuide Wiki
    {
        get
        {
            if (Server.IsIlt && Guide.Guide.Quests.Count > 0) return Guide.Guide;
            QuestGuide sg = ServerGuide.Client.Guide;
            if (sg.Quests.Count == 0) return Fandom.Guide;
            // The active server's own wiki quests (RynthConquest's Conquest wiki, 2026-11-21) over the AC
            // Community Wiki's: its quests first, then the retail ones it doesn't have; the kill tasks, society
            // quests and contracts are the AC Community Wiki's.
            int key = unchecked(ServerGuide.Client.Revision * 7919 + Fandom.Revision);
            if (_merged == null || _mergedKey != key || !ReferenceEquals(_mergedFrom, sg))
            {
                var g = new QuestGuide
                {
                    Source = sg.Source + "+fandom", Parser = Math.Min(sg.Parser, Fandom.Guide.Parser),
                    UpdatedUtc = sg.UpdatedUtc, KillTasks = Fandom.Guide.KillTasks, Society = Fandom.Guide.Society, Contracts = Fandom.Guide.Contracts,
                };
                var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (GuideQuest q in sg.Quests) if (titles.Add(q.Title)) g.Quests.Add(q);
                foreach (GuideQuest q in Fandom.Guide.Quests) if (titles.Add(q.Title)) g.Quests.Add(q);
                _merged = g;
                _mergedKey = key;
                _mergedFrom = sg;
            }
            return _merged;
        }
    }

    private QuestGuide? _merged, _mergedFrom;
    private int _mergedKey;

    /// <summary>The active server's own wiki quest guide (RynthServer.QuestGuide; RynthConquest's), on any server but ILT.</summary>
    public readonly ServerGuideClient ServerGuide = new();

    /// <summary>The wiki tab's name: "ILT Wiki", "&lt;server&gt; Wiki" for a server's own guide, else "AC Wiki".</summary>
    public string WikiLabel =>
        Server.IsIlt && Guide.Guide.Quests.Count > 0 ? "ILT Wiki"
        : ServerGuide.Client.Guide.Quests.Count > 0 ? (ServerGuide.Client.Tag.Length > 0 ? ServerGuide.Client.Tag : "Server") + " Wiki"
        : "AC Wiki";

    /// <summary>The wiki in use is the AC Community Wiki's.</summary>
    public bool WikiIsFandom => ReferenceEquals(Wiki, Fandom.Guide);

    /// <summary>Goes up when either guide changes.</summary>
    public int WikiRevision => unchecked((Guide.Revision * 7919 + Fandom.Revision) * 31 + ServerGuide.Client.Revision);

    /// <summary>A wiki quest by title: the guide in use, else the AC Community Wiki's (a contract's quest, a society quest).</summary>
    public GuideQuest? FindWiki(string title) => Wiki.Find(title) ?? (WikiIsFandom ? null : Fandom.Guide.Find(title));
    /// <summary>"Run this quest": the wiki quest RynthNav is walking (set up by the plugin's Initialize).</summary>
    public QuestRunner? Runner;
    /// <summary>Stamps the server gave this session ("You've stamped X!"), as GuideStamp keys: done before /myquests says so.</summary>
    public readonly System.Collections.Generic.HashSet<string> SessionStamps = new(StringComparer.Ordinal);
    /// <summary>Goes up when <see cref="SessionStamps"/> grows.</summary>
    public int SessionStampRevision;
    /// <summary>Kill tasks' kill lines this session (the Kill Tasks tab, 2026-11-21).</summary>
    public readonly KillChat Kills = new();
    /// <summary>Sends a RynthAi command through the plugin-command broker (action, value); false without RynthAi.</summary>
    public Func<string, string, bool> SendAi = (_, _) => false;
    /// <summary>RynthILT's data interface: the Temple guardians (Infinite Leaftide only).</summary>
    public readonly RynthCore.Plugin.Shared.RynthIltDataClient IltData = new();
    /// <summary>The Temple guardians as RynthILT last reported them (None off Infinite Leaftide).</summary>
    public RynthCore.Plugin.Shared.IltGuardianView Guardian = RynthCore.Plugin.Shared.IltGuardianView.None;
    /// <summary>Goes up when <see cref="Guardian"/> changes (the Quests list rebuilds its guardian rows).</summary>
    public int GuardianRevision;
    /// <summary>The last guardian command's problem, or "".</summary>
    public string GuardianMessage = "";
    /// <summary>A verb for RynthILT's Temple guardians (handin, stop, refresh, chat/auto/buy on|off).</summary>
    public Func<string, bool> GuardianCommand = _ => false;
    // A server's own screens are its server plugin's (2026-10-20, 2026-10-21): nothing here is one server's.
    /// <summary>RynthAi's session (the one session every tracker shows); read while the Character tab shows.</summary>
    public readonly RynthCore.Plugin.Shared.RynthAiSessionClient AiSession = new();
    /// <summary>The last session RynthAi reported; SessionKnown false without RynthAi.</summary>
    public RynthCore.Plugin.Shared.SessionStatsV1 Session;
    public bool SessionKnown;
    /// <summary>RynthCore's server list (API v86) and the active server owner's settings; State null on older engines.</summary>
    public readonly RynthCore.Plugin.Shared.ServerStateClient ServerState = new();
    /// <summary>
    /// The active server plugin's quest pack ("RynthServer.QuestPack"): whatever the plugin RynthCore names
    /// as the active server's owner offers; none without one (retail quests only).
    /// </summary>
    public readonly RynthCore.Plugin.Shared.ServerQuestPackClient QuestPacks = new();
    /// <summary>
    /// The active server's Void DoT duration rule: its plugin's profile value "voidDotDurationScale"
    /// (RynthConquest publishes Conquest's), 1 on every other server or without the plugin.
    /// </summary>
    public double VoidDotScale = 1.0;
    /// <summary>The Gear tab's Check (worn gear's spells, wards and banes, ratings); every server.</summary>
    public readonly GearScanner Gear = new();
    /// <summary>The T11 Tracking tab's packs and bounty slots (Infinite Leaftide only): read while that tab shows.</summary>
    public readonly T11Scanner T11 = new();
    /// <summary>Set by the plugin: reads the packs and bounty slots for the T11 Tracking tab now.</summary>
    public Action RescanT11 = () => { };

    /// <summary>Titles the character holds (engine API v75); TitlesKnown false until the engine has the list.</summary>
    public readonly System.Collections.Generic.HashSet<int> EarnedTitles = new();
    public bool TitlesKnown;
    public uint CurrentTitle;

    public bool InWorld;
    public long NowMs;
    public DateTime UtcNow;
    /// <summary>The last thing the quest list check said, shown on the About tab.</summary>
    public string UpdaterMessage = "";

    /// <summary>Set by the plugin: sends /myquests (reply hidden when the setting says so). False when nothing was sent (not in world, or one went out moments ago).</summary>
    public Func<bool> RequestQuests = () => false;

    /// <summary>Set by the plugin (the Refresh buttons): re-reads the character's stats and buffs, and RynthAi's session, at once.</summary>
    public Action RereadCharacter = () => { };

    /// <summary>Set by the plugin: re-reads the titles the engine holds for the character.</summary>
    public Action RereadTitles = () => { };

    /// <summary>Set by the plugin: reloads the quest list and server pack from their files and re-asks RynthCore which server this is.</summary>
    public Action ReloadQuestList = () => { };

    /// <summary>Where <see cref="Print"/> goes instead of the chat window (the offline tests).</summary>
    public Action<string>? PrintSink = null;

    /// <summary>Set by the plugin: sends a chat command to the server as if typed (the views' buttons).</summary>
    public Func<string, bool> Send = _ => false;

    /// <summary>Set by the plugin: runs a chat command through RynthCore's plugin dispatch (another plugin's window).</summary>
    public Func<string, bool> RunCommand = _ => false;

    /// <summary>Set by the plugin: "&lt;verb&gt; &lt;targets&gt;" for RynthNav's /rnav (go, route, arrow); false when RynthNav isn't there.</summary>
    public Func<string, bool> SendNav = _ => false;

    /// <summary>Set by the plugin: shows or hides the Quest Timers window.</summary>
    public Action<bool> SetQuestTimersWindow = _ => { };
    /// <summary>Set by the plugin: whether the Quest Timers window is showing.</summary>
    public Func<bool> QuestTimersWindowVisible = () => false;

    public Func<uint, int> Props => id => Character.Int(Host, id);

    /// <summary>
    /// The plugin's chat lines, queued and written by the tick (Plugins/Shared/CommandReply.cs: the host
    /// drops a line within 100 ms of another plugin's, so a quest's several lines used to go missing).
    /// Null before Initialize (and in the offline tests, which use <see cref="PrintSink"/>).
    /// </summary>
    public CommandReply? Reply;

    /// <summary>A line in the player's chat window only (nothing is sent to the server).</summary>
    public void Print(string text) => Print(text, ok: true);

    /// <summary>A line saying a command didn't work (the failure colour).</summary>
    public void PrintFail(string text) => Print(text, ok: false);

    private void Print(string text, bool ok)
    {
        if (PrintSink != null) { PrintSink(text); return; }
        if (Reply != null) { Reply.Say(ok, text); return; }
        if (!Host.WriteToChat("[RynthOracle] " + text, ok ? CommandReply.OkChatType : CommandReply.FailChatType))
            Host.Log("[RynthOracle] " + text);
    }

    /// <summary>A click on a quest: its info and hint text, link and flag history in chat (upstream "thinks" them).</summary>
    public void PrintQuest(Quest q)
    {
        foreach (string line in QuestText.Lines(q, Flags, Book, UtcNow)) Print(line);
    }
}
