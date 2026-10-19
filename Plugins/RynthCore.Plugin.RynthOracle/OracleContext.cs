using System;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthOracle.Data;
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
    /// <summary>RynthILT's server profile (Leaftide features, fellowship rules); None without RynthILT.</summary>
    public readonly IltServerLink Ilt = new();
    /// <summary>RynthILT's "RynthILT.Data" (the Character tab's charms list and Toggle buttons).</summary>
    public readonly RynthCore.Plugin.Shared.RynthIltDataClient IltData = new();
    /// <summary>The charms RynthILT last reported (None without RynthILT); read while the Character tab shows.</summary>
    public RynthCore.Plugin.Shared.IltCharmsView Charms = RynthCore.Plugin.Shared.IltCharmsView.None;
    /// <summary>RynthAi's session (the one session every tracker shows); read while the Character tab shows.</summary>
    public readonly RynthCore.Plugin.Shared.RynthAiSessionClient AiSession = new();
    /// <summary>The last session RynthAi reported; SessionKnown false without RynthAi.</summary>
    public RynthCore.Plugin.Shared.SessionStatsV1 Session;
    public bool SessionKnown;
    /// <summary>The last charm Toggle's outcome, shown under the charms list.</summary>
    public string CharmMessage = "";
    /// <summary>RynthILT's pet breeding tracker (None without it); read while the Pet Breeding tab shows.</summary>
    public RynthCore.Plugin.Shared.IltBreedingView Breeding = RynthCore.Plugin.Shared.IltBreedingView.None;
    /// <summary>The last breeding button's outcome, shown under the tab's buttons.</summary>
    public string BreedingMessage = "";

    /// <summary>Set by the plugin: sends a verb to RynthILT's breeding tracker (RynthIltDataApi PetBreedingCommand).</summary>
    public Func<string, bool> BreedingCommand = _ => false;
    /// <summary>RynthILT's Temple guardians (None without RynthILT 0.2.20+); read while the Quests tab shows on Infinite Leaftide.</summary>
    public RynthCore.Plugin.Shared.IltGuardianView Guardian = RynthCore.Plugin.Shared.IltGuardianView.None;
    /// <summary>Goes up each time <see cref="Guardian"/> changes, so the Quests list rebuilds its guardian rows.</summary>
    public int GuardianRevision;
    /// <summary>The last guardian button's outcome, shown under the guardian controls.</summary>
    public string GuardianMessage = "";
    /// <summary>Set by the plugin: sends a verb to RynthILT's guardians (RynthIltDataApi GuardianCommand).</summary>
    public Func<string, bool> GuardianCommand = _ => false;
    /// <summary>RynthCore's server list (API v86) and the active server owner's settings; State null on older engines.</summary>
    public readonly RynthCore.Plugin.Shared.ServerStateClient ServerState = new();
    /// <summary>ConquestAC-only data (tag Conquest); fed only while on ConquestAC.</summary>
    public readonly ConquestData Conquest = new();
    /// <summary>Aelrynth's leaderboards (tag Aelrynth); fed only while on Aelrynth.</summary>
    public readonly AelrynthBoards Boards = new();

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

    /// <summary>Set by the plugin: asks RynthILT to use a carried charm or Guardian Hand (its toggle id).</summary>
    public Func<uint, bool> ToggleCharm = _ => false;

    /// <summary>Set by the plugin: runs a chat command through RynthCore's plugin dispatch (another plugin's window).</summary>
    public Func<string, bool> RunCommand = _ => false;

    /// <summary>Set by the plugin: shows or hides the Quest Timers window.</summary>
    public Action<bool> SetQuestTimersWindow = _ => { };
    /// <summary>Set by the plugin: whether the Quest Timers window is showing.</summary>
    public Func<bool> QuestTimersWindowVisible = () => false;

    public Func<uint, int> Props => id => Character.Int(Host, id);

    /// <summary>A line in the player's chat window only (nothing is sent to the server).</summary>
    public void Print(string text)
    {
        if (PrintSink != null) { PrintSink(text); return; }
        if (!Host.WriteToChat("[RynthOracle] " + text, 1))
            Host.Log("[RynthOracle] " + text);
    }

    /// <summary>A click on a quest: its info and hint text, link and flag history in chat (upstream "thinks" them).</summary>
    public void PrintQuest(Quest q)
    {
        foreach (string line in QuestText.Lines(q, Flags, Book, UtcNow)) Print(line);
    }
}
