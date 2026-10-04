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
    public readonly QuestCatalogUpdater Updater = new();
    public readonly CharacterSnapshot Character = new();
    public readonly VoidTracker Void = new();
    public readonly FellowshipTracker Fellowship = new();
    public readonly ServerIdentity Server = new();
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

    /// <summary>Set by the plugin: sends /myquests (reply hidden when the setting says so).</summary>
    public Action RequestQuests = () => { };

    /// <summary>Set by the plugin: sends a chat command to the server as if typed (the views' buttons).</summary>
    public Func<string, bool> Send = _ => false;

    public Func<uint, int> Props => id => Character.Int(Host, id);

    /// <summary>A line in the player's chat window only (nothing is sent to the server).</summary>
    public void Print(string text)
    {
        if (!Host.WriteToChat("[RynthOracle] " + text, 1))
            Host.Log("[RynthOracle] " + text);
    }
}
