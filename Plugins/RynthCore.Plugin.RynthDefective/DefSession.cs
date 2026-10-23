using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Use;

namespace RynthCore.Plugin.RynthDefective;

/// <summary>
/// What the windows and the commands act on: the character's data, the use controller, and the
/// things a player does to slots (set one from the selected item, clear, move, bind a key). The
/// host-facing parts are delegates, so all of it runs offline in the tests.
/// </summary>
internal sealed class DefSession
{
    public DefData Data = new();
    public readonly UseController Use = new();
    public readonly HotkeyLookup Keys;

    /// <summary>Everything carried now (cached by the plugin; at most a moment old).</summary>
    public Func<IReadOnlyList<CarriedItem>> Items = () => Array.Empty<CarriedItem>();
    public Func<uint> SelectedId = () => 0;
    public Func<long> Now = () => Environment.TickCount64;
    /// <summary>Types a line to the engine's chat parser ("/rc hotkey add F9 /rdef use 3"); false when it can't.</summary>
    public Func<string, bool> RunChat = _ => false;
    /// <summary>Asks for the character's file to be written soon.</summary>
    public Action Save = () => { };
    /// <summary>The pictures and bar buttons need op level 2 script windows.</summary>
    public bool IconsAvailable;
    public bool LoggedIn;
    public string Character = string.Empty;

    // The line under the bar: the last thing that happened.
    private string _status = string.Empty;
    private long _statusAtMs;
    private bool _statusIsWarning;
    public const long StatusShownMs = 9000;

    public DefSession(HotkeyLookup keys) => Keys = keys;

    public DefSettings Settings => Data.Settings;

    public void SetStatus(string text, bool warning = false)
    {
        _status = text;
        _statusIsWarning = warning;
        _statusAtMs = Now();
    }

    /// <summary>The status line while it is fresh; empty after <see cref="StatusShownMs"/>.</summary>
    public string Status(out bool warning)
    {
        warning = _statusIsWarning;
        return _status.Length > 0 && Now() - _statusAtMs < StatusShownMs ? _status : string.Empty;
    }

    // ── Using ───────────────────────────────────────────────────────────────

    /// <summary>The slot's button or key: use what it holds. Returns what to tell the player when nothing could be queued.</summary>
    public string? UseSlot(int slotNumber)
    {
        if (slotNumber < 1 || slotNumber > DefSettings.SlotsMax) return $"There is no slot {slotNumber}.";
        DefSlot slot = Data.Slots[slotNumber - 1];
        if (slot.Item == null) return $"Slot {slotNumber} is empty. Set it from the bar (Edit slots) or with /rdef set {slotNumber}.";
        Use.Request(new UseRequest(slot.Item, null, slot.Mode, "slot " + slotNumber, Now()));
        return null;
    }

    /// <summary>/rdef use &lt;name&gt;: an item by what the player typed.</summary>
    public void UseByName(string name) =>
        Use.Request(new UseRequest(null, name, UseMode.Auto, "name '" + name + "'", Now()));

    /// <summary>The picker: wield this weapon.</summary>
    public void Wield(ItemRef item) =>
        Use.Request(new UseRequest(item, null, UseMode.Equip, "picker", Now()));

    // ── Slots ───────────────────────────────────────────────────────────────

    public string SetFromSelected(int slotNumber)
    {
        uint sel = SelectedId();
        if (sel == 0) return "Select the item in the game first (click it in your pack), then set the slot.";
        CarriedItem? it = FindById(sel);
        if (it == null) return "The selected object isn't in your pack, so it can't go in a slot.";
        return SetSlot(slotNumber, it);
    }

    /// <summary>An item dragged onto the slot (its object id).</summary>
    public string SetFromId(int slotNumber, uint objectId)
    {
        CarriedItem? it = FindById(objectId);
        if (it == null) return "That isn't in your pack, so it can't go in a slot.";
        return SetSlot(slotNumber, it);
    }

    public string SetFromName(int slotNumber, string name)
    {
        CarriedItem? it = ItemMatcher.FindByName(name, Items());
        if (it == null) return $"No item named '{name}' in your pack.";
        return SetSlot(slotNumber, it);
    }

    public string SetSlot(int slotNumber, CarriedItem it)
    {
        if (slotNumber < 1 || slotNumber > DefSettings.SlotsMax) return $"There is no slot {slotNumber}.";
        DefSlot slot = Data.Slots[slotNumber - 1];
        slot.Item = ItemRef.From(it);
        slot.Mode = UseMode.Auto;
        Save();
        ItemKind kind = ItemKinds.Classify(it);
        return $"Slot {slotNumber}: {it.FullName} ({UseModes.Describe(ItemKinds.DefaultAction(kind))} when pressed).";
    }

    public string ClearSlot(int slotNumber)
    {
        if (slotNumber < 1 || slotNumber > DefSettings.SlotsMax) return $"There is no slot {slotNumber}.";
        Data.Slots[slotNumber - 1] = new DefSlot();
        Save();
        return $"Slot {slotNumber} cleared.";
    }

    /// <summary>Swaps slot <paramref name="a"/> with <paramref name="b"/> (1-based), as moving one place.</summary>
    public bool SwapSlots(int a, int b)
    {
        int max = Settings.SlotCount;
        if (a < 1 || b < 1 || a > max || b > max || a == b) return false;
        (Data.Slots[a - 1], Data.Slots[b - 1]) = (Data.Slots[b - 1], Data.Slots[a - 1]);
        Save();
        return true;
    }

    public void SetSlotMode(int slotNumber, UseMode mode)
    {
        Data.Slots[slotNumber - 1].Mode = mode;
        Save();
    }

    public void SetSlotCount(int count)
    {
        Settings.SlotCount = Math.Clamp(count, DefSettings.SlotsMin, DefSettings.SlotsMax);
        Save();
    }

    public CarriedItem? FindById(uint id)
    {
        foreach (CarriedItem it in Items())
            if (it.Id == id) return it;
        return null;
    }

    /// <summary>
    /// After an inventory read: every slot, favourite and recent entry follows the item it stands for
    /// (a used-up stack's replacement, a new picture). Never clears anything. Returns whether a
    /// save is due. Skipped while nothing at all is carried (the inventory isn't loaded yet).
    /// </summary>
    public bool Maintain(IReadOnlyList<CarriedItem> items)
    {
        if (items.Count == 0) return false;
        bool changed = false;
        foreach (DefSlot s in Data.Slots)
            if (s.Item != null && ItemMatcher.Maintain(s.Item, items)) changed = true;
        foreach (ItemRef f in Data.Favourites)
            if (ItemMatcher.Maintain(f, items)) changed = true;
        foreach (ItemRef r in Data.Recent)
            if (ItemMatcher.Maintain(r, items)) changed = true;
        return changed;
    }

    // ── Keys ────────────────────────────────────────────────────────────────

    /// <summary>The key bound to slot <paramref name="slotNumber"/> or to its item by name, or null.</summary>
    public string? KeyOfSlot(int slotNumber)
    {
        long now = Now();
        string? key = Keys.KeyFor(DefCommandParser.UseCommand(slotNumber), now);
        if (key != null) return key;
        ItemRef? item = Data.Slots[slotNumber - 1].Item;
        return item == null ? null : Keys.KeyFor(NameCommand(item), now);
    }

    public static string NameCommand(ItemRef item) => "/rdef use " + item.FullName;

    /// <summary>Binds a key to the slot ("/rdef use 3") or, with <paramref name="byName"/>, to its item's name ("/rdef use Stamina Kit"). Returns the message.</summary>
    public string BindKey(int slotNumber, string combo, bool byName)
    {
        combo = (combo ?? string.Empty).Trim();
        if (combo.Length == 0) return "Type the key first, e.g. F9 or Ctrl+1.";
        if (combo.IndexOf(' ') >= 0) return "A key has no spaces, e.g. Ctrl+F9.";
        if (slotNumber < 1 || slotNumber > DefSettings.SlotsMax) return $"There is no slot {slotNumber}.";
        ItemRef? item = Data.Slots[slotNumber - 1].Item;
        if (byName && item == null) return $"Slot {slotNumber} is empty: nothing to bind by name.";

        string command = byName ? NameCommand(item!) : DefCommandParser.UseCommand(slotNumber);
        UnbindQuiet(slotNumber);
        if (!RunChat($"/rc hotkey add {combo} {command}"))
            return $"This engine can't bind keys from here. Type: /rc hotkey add {combo} {command}";
        Keys.Invalidate();
        return $"Binding {combo} to {command} (the engine confirms in chat; Settings > Hotkeys lists it).";
    }

    public string UnbindKey(int slotNumber)
    {
        if (slotNumber < 1 || slotNumber > DefSettings.SlotsMax) return $"There is no slot {slotNumber}.";
        string? key = UnbindQuiet(slotNumber);
        Keys.Invalidate();
        return key == null ? $"Slot {slotNumber} has no key." : $"Key {key} removed from slot {slotNumber}.";
    }

    /// <summary>Removes the slot's current key (either way of binding); returns it.</summary>
    private string? UnbindQuiet(int slotNumber)
    {
        string? key = KeyOfSlot(slotNumber);
        if (key == null) return null;
        string combo = key.EndsWith(" (off)", StringComparison.Ordinal) ? key[..^6] : key;
        RunChat($"/rc hotkey remove {combo}");
        return combo;
    }

    // ── Favourites ──────────────────────────────────────────────────────────

    public string ToggleFavourite(ItemRef r)
    {
        bool now = Data.ToggleFavourite(r);
        Save();
        return now ? $"{r.FullName} is a favourite." : $"{r.FullName} is no longer a favourite.";
    }

    /// <summary>/rdef fav: the item selected in the game.</summary>
    public string ToggleSelectedFavourite()
    {
        uint sel = SelectedId();
        if (sel == 0) return "Select a weapon in the game first.";
        CarriedItem? it = FindById(sel);
        if (it == null) return "The selected object isn't in your pack.";
        return ToggleFavourite(ItemRef.From(it));
    }
}
