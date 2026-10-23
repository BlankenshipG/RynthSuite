using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using RynthCore.Plugin.RynthDefective.Items;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Ui;
using RynthCore.Plugin.RynthDefective.Use;
using RynthCore.Plugin.RynthDefective.Views;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDefective;

/// <summary>
/// RynthDefective: an item bar with one-touch use, and a fast weapon picker.
///
/// A slot holds one item identity (Model/ItemRef.cs: the object id, plus name, wcid and material).
/// Pressing the slot, or the key the engine's hotkeys bind to "/rdef use N", uses the item without
/// anything selected in the game: a kit on yourself, a potion or scroll by itself, a weapon or wand
/// wielded (Use/UseController.cs has the rules, the busy handling and the stance). When the exact
/// object is gone the slot follows another carried item of the same identity.
///
/// The weapon picker window lists the carried weapons and wands by kind, with search, favourites and
/// the recently wielded, and wields one with one click.
///
/// Everything runs on the plugin pump (OnTick and the events the engine drains into it) except
/// OnChatBarEnter, which the engine may call on AC's thread: it only queues the command.
/// </summary>
public sealed partial class RynthDefectivePlugin : RynthPluginBase
{
    public const string Version = "0.1.0";
    private const long IdentityRetryMs = 1000;
    private const long SaveDelayMs = 750;
    private const long PassIntervalMs = 400;
    private const long InventoryAgeMs = 700;

    private readonly DefStore _store = new();
    private readonly DefSession _session;
    private readonly UiHost _ui = new();
    private readonly ConcurrentQueue<string> _commands = new();
    private HostDefWorld? _world;
    private BarView? _bar;
    private PickerView? _picker;
    private UiWindow? _barWindow, _pickerWindow;

    private bool _inWorld;
    private bool _identityKnown;
    private long _nextIdentityTryMs;
    private string _server = string.Empty;
    private string _character = string.Empty;
    private long _saveDueMs;
    private bool _noUiLogged;
    private int _revisionSeen = -1;

    public RynthDefectivePlugin()
    {
        _session = new DefSession(new HotkeyLookup());
    }

    public override int Initialize()
    {
        _ui.Log = msg => Host.Log(msg);
        // The shortcut bar (plugin API v90) draws with ImGui.NET bound to the engine's cimgui, in its look.
        RynthCore.Plugin.Shared.ImGuiNativeBinding.Ensure(msg => Host.Log("[RynthDefective] " + msg));
        RynthCore.Plugin.Shared.RynthUi.RynthUi.Init(Host);
        _world = new HostDefWorld(Host);
        _session.Items = () => _world.Snapshot(InventoryAgeMs);
        _session.SelectedId = () => Host.HasGetSelectedItemId ? Host.GetSelectedItemId() : 0;
        _session.RunChat = line => Host.HasInvokeChatParser && Host.InvokeChatParser(line);
        _session.Save = RequestSave;

        _session.Use.Say = text => { _session.SetStatus(text); Host.Log("[RynthDefective] " + text); };
        _session.Use.Warn = text => { _session.SetStatus(text, warning: true); Print(text); };
        _session.Use.Log = line => Host.Log(line);
        _session.Use.Wielded = OnWielded;

        _bar = new BarView(_session);
        _picker = new PickerView(_session);
        _barWindow = _ui.Add(new UiWindow("RynthDefective/Bar", "Defective") { DefaultWidth = 330f, DefaultHeight = 160f, MinWidth = 220f, MinHeight = 100f });
        _pickerWindow = _ui.Add(new UiWindow("RynthDefective/Weapons", "Defective Weapons") { DefaultWidth = 360f, DefaultHeight = 480f, MinWidth = 260f, MinHeight = 160f });
        _bar.RequestFit = FitBar;
        _bar.ShowPicker = () => ShowPicker(true);
        Host.Log($"[RynthDefective] {Version} initialized. /rdef shows or hides the bar; /rdef use <slot or item name>; /rdef help. Files: {_store.Root}");
        return 0;
    }

    // The engine calls this on a real login and on a freshly (re)loaded plugin already in game.
    public override void OnLoginComplete()
    {
        _inWorld = true;
        _identityKnown = false;
        _nextIdentityTryMs = Environment.TickCount64;
        _session.Use.Reset();
        _world?.Reset();
    }

    public override void OnLogout()
    {
        SaveNow();
        _inWorld = false;
        _identityKnown = false;
        _session.LoggedIn = false;
        _session.Use.Reset();
        _world?.Reset();
        _barView = RynthCore.Plugin.RynthDefective.ShortcutBar.BarSnapshot.NotReady;
        foreach (UiWindow? w in new[] { _barWindow, _pickerWindow })
            if (w != null) { w.Visible = false; w.NeedsPass = true; }
        _ui.SubmitEmpty(Host);
    }

    public override void Shutdown()
    {
        SaveNow();
        _session.Use.Reset();
        _ui.SubmitEmpty(Host);
    }

    // ── Chat ────────────────────────────────────────────────────────────────

    // May run on AC's thread: classify and queue only.
    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string t = text.Trim();
        if (!t.StartsWith("/rdef", StringComparison.OrdinalIgnoreCase) || (t.Length > 5 && !char.IsWhiteSpace(t[5])))
            return;
        eat = 1;
        _commands.Enqueue(t.Length > 5 ? t.Substring(5).Trim() : string.Empty);
    }

    private void RunCommand(string arg)
    {
        DefCommand c = DefCommandParser.Parse(arg);
        if (c.Verb == DefVerb.Help)
        {
            PrintHelp();
            return;
        }
        if (c.Verb == DefVerb.Error)
        {
            Print(c.Error);
            return;
        }
        if (!_identityKnown)
        {
            Print("Log in first.");
            return;
        }

        switch (c.Verb)
        {
            case DefVerb.Toggle:
                if (UsesShortcutBar)
                    Host.SetPanelOpen(RynthCore.Plugin.RynthDefective.ShortcutBar.ShortcutBarWindow.Title, RynthPanelOpen.Toggle);
                else if (_barWindow != null) { _barWindow.Visible = !_barWindow.Visible; _barWindow.NeedsPass = true; NoteNoUi(_barWindow.Visible); }
                break;
            case DefVerb.Selection when c.Text.Length > 0:
                if (uint.TryParse(c.Text.Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out uint pick))
                    Host.SelectItem(pick);
                break;
            case DefVerb.Selection:
                if (UsesShortcutBar) Host.SetPanelOpen(RynthCore.Plugin.RynthDefective.ShortcutBar.SelectionWindow.Title, RynthPanelOpen.Toggle);
                else Print("This RynthCore can't show the selection window (it needs plugin API v90).");
                break;
            case DefVerb.Panels:
                if (UsesPanelButtons) Host.SetPanelOpen(RynthCore.Plugin.RynthDefective.ShortcutBar.PanelButtonsWindow.Title, RynthPanelOpen.Toggle);
                else Print("This RynthCore can't open the game's panels from buttons (it needs plugin API v90).");
                break;
            case DefVerb.Weapons:
                if (c.Text.Length > 0 && _picker != null) _picker.Search = c.Text;
                ShowPicker(c.Text.Length > 0 || _pickerWindow?.Visible != true);
                break;
            case DefVerb.Use:
                if (c.Slot > 0)
                {
                    string? problem = _session.UseSlot(c.Slot);
                    if (problem != null) { _session.SetStatus(problem, warning: true); Print(problem); }
                }
                else _session.UseByName(c.Text);
                break;
            case DefVerb.Set:
                Print(c.Text.Length > 0 ? _session.SetFromName(c.Slot, c.Text) : _session.SetFromSelected(c.Slot));
                break;
            case DefVerb.Clear:
                Print(_session.ClearSlot(c.Slot));
                break;
            case DefVerb.Bind:
                Print(_session.BindKey(c.Slot, c.Text, byName: false));
                break;
            case DefVerb.Unbind:
                Print(_session.UnbindKey(c.Slot));
                break;
            case DefVerb.Favourite:
                Print(_session.ToggleSelectedFavourite());
                break;
            case DefVerb.Slots:
                _session.SetSlotCount(c.Slot);
                FitBar();
                Print($"{_session.Settings.SlotCount} slots.");
                break;
            case DefVerb.List:
                PrintSlots();
                break;
            case DefVerb.Status:
                PrintStatus();
                break;
        }
        MarkAllForPass();
    }

    private void PrintHelp()
    {
        Print(DefCommandParser.Usage);
        Print("A key for a slot: /rdef bind 1 F9 (or Edit slots in the bar). Any item by name from Settings > Hotkeys: /rc hotkey add F9 /rdef use Stamina Kit.");
    }

    private void PrintSlots()
    {
        IReadOnlyList<CarriedItem> items = _session.Items();
        var sb = new StringBuilder();
        for (int i = 1; i <= _session.Settings.SlotCount; i++)
        {
            DefSlot slot = _session.Data.Slots[i - 1];
            if (slot.Item == null) { Print($"{i}: (empty)"); continue; }
            SlotStatus s = ItemMatcher.Status(slot.Item, items);
            sb.Clear();
            sb.Append(i).Append(": ").Append(slot.Item.FullName);
            string count = SlotText.Count(s, out _);
            if (count.Length > 0) sb.Append(" (").Append(count).Append(')');
            if (_session.KeyOfSlot(i) is { } key) sb.Append("  [").Append(key).Append(']');
            Print(sb.ToString());
        }
    }

    private void PrintStatus()
    {
        int filled = 0;
        for (int i = 0; i < _session.Settings.SlotCount; i++) if (!_session.Data.Slots[i].Empty) filled++;
        Print($"{_character}: {filled}/{_session.Settings.SlotCount} slots set, {_session.Data.Favourites.Count} favourite weapons; "
              + $"{(_session.Use.InFlight ? "an action is in flight" : _session.Use.HasPending ? "an action is waiting" : "idle")}; file {_store.PathFor(_server, _character)}");
    }

    private void Print(string text)
    {
        if (!Host.WriteToChat("[RynthDefective] " + text, 1))
            Host.Log("[RynthDefective] " + text);
    }

    private void NoteNoUi(bool shown)
    {
        if (shown && !Host.HasUi && !_noUiLogged)
        {
            _noUiLogged = true;
            Print("This RynthCore engine can't show plugin windows (it needs script windows, API v71, with ImGui on). /rdef use <slot or item name> still works.");
        }
    }

    // ── Windows ─────────────────────────────────────────────────────────────

    private void ShowPicker(bool visible)
    {
        if (_pickerWindow == null) return;
        _pickerWindow.Visible = visible;
        _pickerWindow.NeedsPass = true;
        NoteNoUi(visible);
    }

    private void FitBar()
    {
        if (_barWindow == null || _bar == null) return;
        (float w, float h) = _bar.WantedSize();
        _barWindow.RequestWidth = w;
        _barWindow.RequestHeight = h;
        _barWindow.NeedsPass = true;
    }

    private void MarkAllForPass()
    {
        if (_barWindow != null) _barWindow.NeedsPass = true;
        if (_pickerWindow != null) _pickerWindow.NeedsPass = true;
    }

    /// <summary>A weapon (or shield) went into the hands: first in the picker's recent list.</summary>
    private void OnWielded(CarriedItem it)
    {
        ItemKind kind = ItemKinds.Classify(it);
        if (!ItemKinds.IsWeapon(kind) && kind != ItemKind.Shield) return;
        _session.Data.NoteRecent(ItemRef.From(it));
        RequestSave();
    }

    // ── Tick ────────────────────────────────────────────────────────────────

    public override void OnTick()
    {
        long now = Environment.TickCount64;
        while (_commands.TryDequeue(out string? cmd))
        {
            try { RunCommand(cmd); }
            catch (Exception ex) { Host.Log($"[RynthDefective] /rdef {cmd} failed: {ex.GetType().Name}: {ex.Message}"); }
        }
        _ui.Poll(Host);

        if (_inWorld)
        {
            if (!_identityKnown && now >= _nextIdentityTryMs) TryStartCharacter(now);
            if (_identityKnown && _world != null)
            {
                try
                {
                    TickInventory();
                    _session.Use.Tick(_world);
                }
                catch (Exception ex)
                {
                    Host.Log($"[RynthDefective] tick failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        if (_saveDueMs != 0 && now >= _saveDueMs) SaveNow();
        TickBar(now);
        TickSelection(now);

        // With the shortcut bar the old script-window bar stays shut (the weapon picker is still one).
        if (UsesShortcutBar && _barWindow != null && _barWindow.Visible) { _barWindow.Visible = false; _barWindow.NeedsPass = true; }

        if (!Host.HasUi || _barWindow == null || _pickerWindow == null || _bar == null || _picker == null) return;
        if (_session.Use.HasPending || _session.Use.InFlight) _barWindow.NeedsPass = _pickerWindow.NeedsPass = true;
        if (!_session.IconsAvailable) RefreshIconSupport();
        Record(_barWindow, now, _bar.Draw);
        Record(_pickerWindow, now, _picker.Draw);
        _ui.Submit(Host, now);

        if (_identityKnown)
        {
            DefSettings st = _session.Settings;
            if (_barWindow.Visible != st.BarOpen || _pickerWindow.Visible != st.PickerOpen)
            {
                st.BarOpen = _barWindow.Visible;
                st.PickerOpen = _pickerWindow.Visible;
                RequestSave();
            }
        }
    }

    /// <summary>Re-reads the inventory while a window shows or an action waits; slots follow their items.</summary>
    private void TickInventory()
    {
        bool barShown = Environment.TickCount64 - System.Threading.Volatile.Read(ref _barDrawnMs) < 2000;
        bool wanted = (_barWindow?.Visible ?? false) || (_pickerWindow?.Visible ?? false) || barShown || _session.Use.HasPending;
        if (!wanted || _world == null) return;
        List<CarriedItem> items = _world.Snapshot(InventoryAgeMs);
        if (_world.Revision == _revisionSeen) return;
        _revisionSeen = _world.Revision;
        if (_session.Maintain(items)) RequestSave();
        MarkAllForPass();
    }

    private void TryStartCharacter(long now)
    {
        _nextIdentityTryMs = now + IdentityRetryMs;
        uint player = Host.GetPlayerId();
        if (player == 0 || !Host.TryGetObjectName(player, out string name) || string.IsNullOrWhiteSpace(name)) return;
        string server = Host.TryGetWorldName(out string w) ? w : string.Empty;

        _server = server;
        _character = name;
        DefData data = _store.Load(server, name, out string? problem);
        if (problem != null) Host.Log($"[RynthDefective] couldn't read the saved slots ({problem}); using defaults.");
        _session.Data = data;
        _session.Use.Settings = data.Settings;
        _session.Character = name;
        _session.LoggedIn = true;
        _identityKnown = true;
        _revisionSeen = -1;
        RefreshIconSupport();
        if (_barWindow != null && _bar != null)
        {
            _barWindow.Visible = data.Settings.BarOpen;
            (float bw, float bh) = _bar.WantedSize();
            _barWindow.DefaultWidth = bw;
            _barWindow.DefaultHeight = bh;
            _barWindow.NeedsPass = true;
        }
        if (_pickerWindow != null) { _pickerWindow.Visible = data.Settings.PickerOpen; _pickerWindow.NeedsPass = true; }
        Host.Log($"[RynthDefective] item bar for {name} on {server}: {data.Slots.FindAll(s => !s.Empty).Count} slots set; file {_store.PathFor(server, name)}");
    }

    private void RefreshIconSupport()
    {
        _session.IconsAvailable = Host.HasUi && Host.TryGetUiInfo(out UiInfoNative info) && info.OpLevel >= 2;
    }

    private void RequestSave()
    {
        MarkAllForPass();
        if (_saveDueMs == 0) _saveDueMs = Environment.TickCount64 + SaveDelayMs;
    }

    private void SaveNow()
    {
        _saveDueMs = 0;
        if (!_identityKnown) return;
        _session.Settings.Clamp();
        string? error = _store.Save(_server, _character, _session.Data);
        if (error != null) Host.Log("[RynthDefective] save failed: " + error);
    }

    /// <summary>A pass when one is wanted, or on the refresh interval while visible (counts and the status tick on).</summary>
    private void Record(UiWindow w, long now, Action<UiWindow> draw)
    {
        bool due = w.NeedsPass || (w.Visible && now >= w.NextPassMs);
        if (!due) return;
        w.NeedsPass = false;
        w.NextPassMs = now + PassIntervalMs;
        w.Begin();
        try
        {
            draw(w);
        }
        catch (Exception ex)
        {
            w.TextWrapped($"Something went wrong drawing this window: {ex.GetType().Name}: {ex.Message}");
            Host.Log($"[RynthDefective] {w.Key} pass failed: {ex}");
        }
        w.End();
    }
}
