using System;
using RynthCore.Plugin.RynthInventory.Data;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthInventory;

/// <summary>What the window reads and the actions it can take. Plugin pump thread only.</summary>
internal sealed class InventoryContext
{
    public RynthCoreHost Host;
    public readonly InventorySettings Settings = new();
    public InventoryStore Store = new();

    /// <summary>The merged view of every file (and this client's live snapshot); null until first built.</summary>
    public InventoryIndex? Index;
    /// <summary>Bumped whenever <see cref="Index"/> is replaced.</summary>
    public int IndexRevision;
    public int BrokenFiles;
    public bool Loading;

    public bool InWorld;
    public uint Player;
    public string Server = string.Empty;
    public string Account = string.Empty;
    public string Character = string.Empty;
    /// <summary>This character's latest snapshot (never changed once published; a change makes a new one).</summary>
    public CharacterSnapshot? Live;
    public DateTime LastSavedUtc;
    public string LastSaveError = string.Empty;
    /// <summary>The storage container open right now, if it is being recorded (0 none).</summary>
    public uint OpenStorage;
    public string OpenStorageLabel = string.Empty;

    /// <summary>The engine draws icons in script windows (op level 2).</summary>
    public bool IconsAvailable;
    public DateTime UtcNow;

    public Action RequestRescan = () => { };
    public Action RequestReload = () => { };
    /// <summary>Forgets a character (moves its file aside). Returns a message for the player.</summary>
    public Func<CharacterInfo, string> Forget = _ => string.Empty;

    public void Print(string text)
    {
        if (!Host.WriteToChat("[RynthInventory] " + text, 1))
            Host.Log("[RynthInventory] " + text);
    }
}
