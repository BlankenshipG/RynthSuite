using System.Collections.Generic;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>One loose item in a pack, as read this pass (InventoryReader).</summary>
internal sealed class SortItem
{
    public uint Id;
    public string Name = string.Empty;
    /// <summary>ObjectClass number (<see cref="ObjectClasses"/>).</summary>
    public int ObjectClass;
    public uint Wcid;
    public int Stack = 1;
    public int MaxStack = 1;
    /// <summary>Worn or wielded (the reader leaves these out; kept so the planner can refuse them too).</summary>
    public bool Wielded { get; set; }
}

/// <summary>The main pack or one side pack and its loose items (packs and foci are never items).</summary>
internal sealed class SortPack
{
    public uint Id;
    public string Name = string.Empty;
    /// <summary>How many items it holds (ItemsCapacity; 102 for the main pack and 24 for a side pack when unknown).</summary>
    public int Capacity;
    public readonly List<SortItem> Items = new();
}

/// <summary>The character's packs: the main pack and the side packs in order, top to bottom (pack 1 first).</summary>
internal sealed class SortInventory
{
    public SortPack Main = new();
    public readonly List<SortPack> Packs = new();
}
