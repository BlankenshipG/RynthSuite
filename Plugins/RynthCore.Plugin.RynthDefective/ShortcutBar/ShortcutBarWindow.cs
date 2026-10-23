// ============================================================================
//  RynthDefective - ShortcutBar/ShortcutBarWindow.cs
//  The shortcut bar, drawn by RynthDefective in its own frameless window
//  (plugin API v90: RynthCore hosts it, "chrome": "none" + "fitHeight"), in
//  the instrument look of RynthAi's dashboard and the player's colour theme.
//
//    header   SHORTCUTS and the slot count (the drag handle; Ctrl+wheel: the
//             size), then Edit, Weapons (the weapon picker) and Hide.
//    slots    the item's picture, its key in the top-left corner (AC's keymap
//             for slots 1-18, the engine's hotkeys after), the count or "in
//             hand" under it. They fill the window's width and wrap: drag the
//             window wider or narrower for one row or several.
//    edit     the slot numbers show, and - / + at the end take a slot off or
//             add one (the player picks how many; 1-18 are the game's).
//  Drag an item from AC's pack onto a slot: it goes there, as on the retail
//  toolbar (the item stays in the pack). Click a slot: it is used as its key
//  would. Right-click: put the selected item there, or empty it, and its keys: remove one, or add one by pressing
//  it. A slot 1-18 key is AC's own (its keymap, so the retail toolbar answers
//  to it too, in every client); a key past 18 is an engine hotkey running
//  /rdef use N. A key comes off whatever had it, after the player says so.
//
//  AC's render thread. Reads the snapshot the tick built; every click is
//  queued for the tick (IBarActions).
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthDefective.ShortcutBar;

/// <summary>What the bar asks of the plugin: everything runs on its tick.</summary>
internal interface IBarActions
{
    void Use(int number);
    void SetFromSelected(int number);
    void Clear(int number);
    void ChangeSlotCount(int delta);
    /// <summary>An item dragged out of AC's UI was let go over slot <paramref name="number"/>.</summary>
    void DropItem(int number, uint objectId);
    /// <summary>A key was bound or removed: the bar asks for its keys again.</summary>
    void KeysChanged();
    void ShowWeapons();
    void Hide();
}

internal sealed class ShortcutBarWindow
{
    public const string Title = "Shortcuts";

    private readonly RynthCoreHost _host;
    private readonly IBarActions _act;
    private bool _edit;
    private float _k = 1f, _r = 1f;

    public ShortcutBarWindow(RynthCoreHost host, IBarActions actions)
    {
        _host = host;
        _act = actions;
    }

    private float Px(float v) => v * _k;

    /// <summary>A font's size at this scale (its baked size times the text ratio).</summary>
    private float Fs(ImFontPtr f) => f.FontSize * _r;

    /// <summary>
    /// The layout's scale and the text's against the baked font: the window's own size (Settings > UI >
    /// Toolbar, Ctrl+wheel: 30-250%, PanelScale) or, unset, the text size the engine baked for it.
    /// </summary>
    private void Scale()
    {
        float fontK = Math.Max(0.5f, Font(RynthUiFont.Ui10).FontSize / 10f);
        int pct = _host.PanelScale(Title);
        _k = pct > 0 ? pct / 100f : fontK;
        _r = _k / fontK;
    }

    private const float PadX = 7, PadTop = 6, PadBottom = 7, Radius = 4, HeaderH = 18, Gap = 3, ChipSize = 16;

    private uint _bg, _key, _inset, _line, _text, _dim, _faint, _accent;
    private static readonly uint InHandColor = Argb(0xFF73D885), MissingColor = Argb(0xFFF98075),
        Black = Argb(0xFF000000), White = Argb(0xFFFFFFFF);

    private static uint A(uint c, float alpha) => WithAlpha(c, (uint)Math.Clamp(MathF.Round(alpha * 255f), 0f, 255f));

    /// <summary>Draws the bar into the host's window; returns the height it needs (pixels). Render thread.</summary>
    public float Draw(BarSnapshot view, bool popped)
    {
        Scale();
        _bg = Color(RynthUiColor.Background);
        _key = Color(RynthUiColor.KeyFace);
        _inset = Color(RynthUiColor.Inset);
        _line = Color(RynthUiColor.Hairline);
        _text = Color(RynthUiColor.Text);
        _dim = Color(RynthUiColor.Muted);
        _faint = Color(RynthUiColor.Faint);
        _accent = Color(RynthUiColor.Accent);

        Vector2 origin = ImGui.GetCursorScreenPos();
        origin = new Vector2(MathF.Round(origin.X), MathF.Round(origin.Y));
        float width = MathF.Floor(ImGui.GetContentRegionAvail().X);
        if (width < 40) return Px(60);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();

        float height;
        dl.ChannelsSplit(2);
        bool merged = false;
        try
        {
            dl.ChannelsSetCurrent(1);
            float x0 = origin.X + Px(PadX), w = width - 2 * Px(PadX);
            float y = origin.Y + Px(PadTop);
            y = Header(dl, view, x0, y, w);
            y += Px(5);
            y = Slots(dl, view, x0, y, w);
            y += Px(PadBottom);
            height = MathF.Ceiling(y - origin.Y);
            dl.ChannelsSetCurrent(0);
            Frame(dl, origin, new Vector2(width, height));
            dl.ChannelsMerge();
            merged = true;
        }
        finally
        {
            if (!merged) dl.ChannelsMerge();
        }
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));
        SlotMenu(view);
        if (_openKeyPopup)
        {
            _openKeyPopup = false;
            ImGui.OpenPopup(KeyPopupId);
        }
        KeyPopup();
        return height;
    }

    private void Frame(ImDrawListPtr dl, Vector2 min, Vector2 size)
    {
        Vector2 max = min + size;
        float r = Px(Radius);
        dl.AddRectFilled(min, max, A(_bg, 0.94f), r);
        dl.AddLine(new Vector2(min.X + r, min.Y + 1.5f), new Vector2(max.X - r, min.Y + 1.5f), A(White, 0.05f));
        dl.AddRect(min, max, _line, r);
    }

    // ── Header ────────────────────────────────────────────────────────────

    private float Header(ImDrawListPtr dl, BarSnapshot view, float x0, float y, float w)
    {
        float h = Px(HeaderH), chip = Px(ChipSize);
        const int chips = 3;
        float chipsW = chips * chip + (chips - 1) * Px(1);

        ImGui.SetCursorScreenPos(new Vector2(x0, y));
        ImGui.InvisibleButton("##sb_drag", new Vector2(Math.Max(1, w - chipsW - Px(4)), h));
        _host.PanelDragWithLastItem();
        SafeImGui.SetItemTooltip("Drag to move the bar. Ctrl+wheel here: bigger or smaller.\nDrag the window's edge to change how many slots fit in a row.");

        ImFontPtr tf = Font(RynthUiFont.NarrowBold11);
        float size = Fs(tf) * 10f / 11f;
        float tx = Tracked(dl, tf, size, new Vector2(x0, MathF.Round(y + (h - size) * 0.5f)), _text, "SHORTCUTS", 0.18f * size);
        ImFontPtr nf = Font(RynthUiFont.Dash9);
        string count = view.Ready ? view.Slots.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        dl.AddText(nf, Fs(nf), new Vector2(MathF.Round(tx + Px(6)), MathF.Round(y + (h - Fs(nf)) * 0.5f + Px(1))), _faint, count);

        float cx = x0 + w - chipsW, cy = y + (h - chip) * 0.5f;
        if (Chip("##sb_edit", RynthIcons.PencilSimple, new Vector2(cx, cy), _edit ? _accent : _faint,
                _edit ? "Done editing." : "Edit: the slot numbers, and - / + to take a slot off or add one.", _edit))
            _edit = !_edit;
        cx += chip + Px(1);
        if (Chip("##sb_weapons", RynthIcons.Sword, new Vector2(cx, cy), _faint, "Weapons: every weapon and wand you carry, wield one with a click."))
            _act.ShowWeapons();
        cx += chip + Px(1);
        if (Chip("##sb_hide", RynthIcons.X, new Vector2(cx, cy), _faint, "Hide the bar (/rdef shows it again). Its keys keep working."))
            _act.Hide();
        return y + h;
    }

    private bool Chip(string id, string icon, Vector2 pos, uint color, string tip, bool held = false)
    {
        Vector2 size = new(Px(ChipSize), Px(ChipSize));
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hot = ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip(tip);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (hot || held) dl.AddRectFilled(pos, pos + size, _key, Px(3));
        DrawIcon(dl, icon, pos, size, Px(11), hot ? _text : color);
        return clicked;
    }

    // ── Slots ─────────────────────────────────────────────────────────────

    private int _menuSlot;
    private const string MenuId = "##sb_slot_menu";

    private float Slots(ImDrawListPtr dl, BarSnapshot view, float x0, float y, float w)
    {
        if (!view.Ready)
        {
            ImFontPtr f = Font(RynthUiFont.Narrow10);
            dl.AddText(f, Fs(f), new Vector2(x0, y), _faint, "Your shortcuts show once you're in the world.");
            return y + Fs(f) + Px(2);
        }
        float cell = Px(Math.Clamp(view.IconSize, 20, 64)), gap = Px(Gap);
        int cells = view.Slots.Length + (_edit ? 2 : 0);
        int perRow = Math.Max(1, (int)MathF.Floor((w + gap) / (cell + gap)));
        int rows = (cells + perRow - 1) / perRow;

        // Items dragged out of AC's UI: the slots' area takes them (the engine keeps the drop from AC).
        Vector2 areaMin = new(x0, y), areaMax = new(x0 + perRow * (cell + gap), y + rows * (cell + gap));
        uint dropped = _host.HasDropTargets ? _host.DropTarget(Title, areaMin, areaMax, true, out _dragging) : 0;
        int under = SlotAt(ImGui.GetIO().MousePos, x0, y, cell, gap, perRow, view.Slots.Length);
        _dropSlot = _dragging ? under : 0;
        if (dropped != 0 && under > 0) _act.DropItem(under, dropped);

        for (int i = 0; i < cells; i++)
        {
            int col = i % perRow, row = i / perRow;
            Vector2 p = new(MathF.Round(x0 + col * (cell + gap)), MathF.Round(y + row * (cell + gap)));
            if (i < view.Slots.Length) Slot(dl, view.Slots[i], p, cell);
            else EditButton(dl, i == view.Slots.Length ? -1 : 1, p, cell, view.Slots.Length);
        }
        return y + rows * (cell + gap) - gap;
    }

    private bool _dragging;
    private int _dropSlot;

    /// <summary>The slot number (1-based) under <paramref name="m"/>, or 0.</summary>
    private static int SlotAt(Vector2 m, float x0, float y0, float cell, float gap, int perRow, int count)
    {
        if (m.X < x0 || m.Y < y0) return 0;
        int col = (int)((m.X - x0) / (cell + gap)), row = (int)((m.Y - y0) / (cell + gap));
        if (col >= perRow || m.X - x0 - col * (cell + gap) > cell || m.Y - y0 - row * (cell + gap) > cell) return 0;
        int i = row * perRow + col;
        return i < count ? i + 1 : 0;
    }

    private void Slot(ImDrawListPtr dl, BarSlot s, Vector2 p, float cell)
    {
        Vector2 size = new(cell, cell);
        ImGui.SetCursorScreenPos(p);
        bool clicked = ImGui.InvisibleButton(Id("##sb_slot", s.Number), size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight)
                       && ImGui.IsMouseReleased(ImGuiMouseButton.Left);
        bool right = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        bool hot = ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip(s.Tooltip);
        float r = Px(3);

        // The well, the picture, a hairline (the accent under the mouse; green while in hand).
        dl.AddRectFilled(p, p + size, s.Empty ? _inset : _key, r);
        if (!s.Empty)
        {
            uint id = s.ObjectId != 0 ? s.ObjectId : s.SpellId;
            IntPtr tex = id == 0 ? IntPtr.Zero : _host.GetIconTexture(s.ObjectId != 0 ? RynthIconKind.Object : RynthIconKind.Spell, id);
            float inset = Px(2);
            if (tex != IntPtr.Zero)
                dl.AddImageRounded(tex, p + new Vector2(inset, inset), p + size - new Vector2(inset, inset), Vector2.Zero, Vector2.One,
                    s.Tone == CountTone.Missing ? A(White, 0.35f) : White, Px(2));
            else
            {
                ImFontPtr af = Font(RynthUiFont.Narrow9);
                string abbr = Abbrev(s.Name);
                float aw = af.CalcTextSizeA(Fs(af), float.MaxValue, 0f, abbr).X;
                dl.AddText(af, Fs(af), new Vector2(MathF.Round(p.X + (cell - aw) * 0.5f), MathF.Round(p.Y + (cell - Fs(af)) * 0.5f)), _dim, abbr);
            }
        }
        uint border = hot || _dropSlot == s.Number ? _accent : s.Tone == CountTone.InHand ? A(InHandColor, 0.8f) : _line;
        dl.AddRect(p, p + size, border, r, ImDrawFlags.None, _dropSlot == s.Number ? Px(2) : 1f);

        // The key, top left (a dark plate under it so it reads over any picture); edit: the slot number.
        ImFontPtr kf = Font(RynthUiFont.Dash9);
        string label = _edit ? s.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : s.Key;
        if (label.Length > 0)
        {
            float lw = kf.CalcTextSizeA(Fs(kf), float.MaxValue, 0f, label).X;
            Vector2 lp = p + new Vector2(Px(2), Px(1));
            dl.AddRectFilled(lp - new Vector2(Px(1), 0), lp + new Vector2(lw + Px(1), Fs(kf)), A(Black, 0.55f), Px(2));
            dl.AddText(kf, Fs(kf), lp, _edit ? _accent : s.Retail ? _text : _accent, label);
        }
        // The count, bottom right.
        if (!_edit && s.Count.Length > 0)
        {
            string c = s.Count == "in hand" ? "hand" : s.Count;
            float cw = kf.CalcTextSizeA(Fs(kf), float.MaxValue, 0f, c).X;
            Vector2 cp = p + new Vector2(cell - cw - Px(2), cell - Fs(kf) - Px(1));
            dl.AddRectFilled(cp - new Vector2(Px(1), 0), cp + new Vector2(cw + Px(1), Fs(kf)), A(Black, 0.55f), Px(2));
            uint cc = s.Tone == CountTone.InHand ? InHandColor : s.Tone == CountTone.Missing ? MissingColor : _text;
            dl.AddText(kf, Fs(kf), cp, cc, c);
        }

        if (clicked && !s.Empty) _act.Use(s.Number);
        if (right)
        {
            _menuSlot = s.Number;
            ImGui.OpenPopup(MenuId);
        }
    }

    private void EditButton(ImDrawListPtr dl, int delta, Vector2 p, float cell, int count)
    {
        Vector2 size = new(cell, cell);
        bool can = delta < 0 ? count > DefSettings.BarSlotsMin : count < DefSettings.BarSlotsMax;
        ImGui.SetCursorScreenPos(p);
        bool clicked = ImGui.InvisibleButton(delta < 0 ? "##sb_less" : "##sb_more", size);
        bool hot = can && ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip(delta < 0
            ? "One slot fewer (the last one's item stays: add the slot back and it is there)."
            : count < DefSettings.RetailSlots ? "One more slot (the game's own, up to 18)."
            : "One more slot (RynthCore's: the game's toolbar holds 18).");
        float r = Px(3);
        dl.AddRect(p, p + size, hot ? _accent : A(_line, can ? 1f : 0.4f), r);
        DrawIcon(dl, delta < 0 ? RynthIcons.Minus : RynthIcons.Plus, p, size, Px(12), can ? (hot ? _text : _dim) : A(_faint, 0.5f));
        if (clicked && can) _act.ChangeSlotCount(delta);
    }

    private void SlotMenu(BarSnapshot view)
    {
        if (!ImGui.IsPopupOpen(MenuId)) return;
        ImGui.PushStyleColor(ImGuiCol.PopupBg, WithAlpha(_bg, 0xFA));
        ImGui.PushStyleColor(ImGuiCol.Border, _line);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Px(4));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Px(6), Px(5)));
        bool open = ImGui.BeginPopup(MenuId);
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
        if (!open) return;
        try
        {
            BarSlot? s = _menuSlot >= 1 && _menuSlot <= view.Slots.Length ? view.Slots[_menuSlot - 1] : null;
            if (s == null) { ImGui.CloseCurrentPopup(); return; }
            ImGui.TextDisabled($"Slot {s.Number}{(s.Retail ? " (the game's own)" : " (RynthCore's)")}");
            if (s.Name.Length > 0) SafeImGui.Text(s.Name);
            ImGui.Separator();
            bool hasSelection = _host.HasGetSelectedItemId && _host.GetSelectedItemId() != 0;
            if (ImGui.MenuItem("Put the selected item here", null, false, hasSelection)) _act.SetFromSelected(s.Number);
            if (ImGui.MenuItem("Empty this slot", null, false, !s.Empty)) _act.Clear(s.Number);
            ImGui.Separator();
            if (!_host.HasKeyBinding)
            {
                ImGui.TextDisabled(s.AllKeys.Length > 0 ? "Keys: " + s.AllKeys
                    : s.Retail ? "No key: bind UseQuickSlot_" + s.Number + " in Configure Keyboard." : "No key: /rdef bind " + (s.Number - DefSettings.RetailSlots) + " F9");
                return;
            }
            string[] keys = SplitKeys(s.AllKeys);
            ImGui.TextDisabled(keys.Length == 0 ? "No key yet" : keys.Length == 1 ? "Key" : "Keys");
            foreach (string key in keys)
            {
                if (!ImGui.MenuItem("Remove " + key)) continue;
                if (_host.UnbindActionKey(KeyAction(s.Number), key, out string why)) _act.KeysChanged();
                else ShowKeyMessage(s.Number, why);
            }
            if (ImGui.MenuItem(keys.Length == 0 ? "Add a key..." : "Add another key..."))
                StartKeyCapture(s.Number);
            SafeImGui.SetItemTooltip(s.Retail
                ? "Press the key: it becomes this slot's in the game's own keymap, so the retail toolbar and every client use it too."
                : "Press the key: a RynthCore hotkey for this slot (the game's toolbar holds 18).");
        }
        finally { ImGui.EndPopup(); }
    }

    // ── Keys: "press a key" ───────────────────────────────────────────────

    private enum KeyStep { Waiting, Confirm, Message }

    private const string KeyPopupId = "##sb_key_popup";
    private bool _openKeyPopup;
    private int _keySlot;
    private KeyStep _keyStep;
    private string _keyName = string.Empty, _keyText = string.Empty;

    /// <summary>The action a slot's keys run: AC's UseQuickSlot_N for 1-18, the engine command for the rest.</summary>
    public static string KeyAction(int number) =>
        number <= DefSettings.RetailSlots ? "UseQuickSlot_" + number : DefCommandParser.UseCommand(number - DefSettings.RetailSlots);

    private static string[] SplitKeys(string keys) =>
        keys.Length == 0 ? Array.Empty<string>() : keys.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void StartKeyCapture(int number)
    {
        _keySlot = number;
        _keyStep = KeyStep.Waiting;
        _keyName = _keyText = string.Empty;
        _host.BeginKeyCapture();
        _openKeyPopup = true;
    }

    private void ShowKeyMessage(int number, string text)
    {
        _keySlot = number;
        _keyStep = KeyStep.Message;
        _keyText = text;
        _openKeyPopup = true;
    }

    private void KeyPopup()
    {
        if (!ImGui.IsPopupOpen(KeyPopupId))
        {
            if (_keyStep == KeyStep.Waiting && _keySlot != 0) { _host.CancelKeyCapture(); _keySlot = 0; }   // clicked away
            return;
        }
        ImGui.PushStyleColor(ImGuiCol.PopupBg, WithAlpha(_bg, 0xFA));
        ImGui.PushStyleColor(ImGuiCol.Border, _line);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Px(4));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Px(8), Px(7)));
        bool open = ImGui.BeginPopup(KeyPopupId);
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
        if (!open) return;
        try
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Px(230));
            ImGui.TextDisabled($"Slot {_keySlot}{(_keySlot <= DefSettings.RetailSlots ? " (the game's own)" : " (RynthCore's)")}");
            switch (_keyStep)
            {
                case KeyStep.Waiting:
                    SafeImGui.Text("Press the key for this slot.");
                    ImGui.TextDisabled("Hold Ctrl, Alt or Shift with it if you like. Esc cancels.");
                    RynthKeyCapture got = _host.PollKeyCapture(out string text);
                    if (got == RynthKeyCapture.Waiting)
                    {
                        if (text.Length > 0) ImGui.TextColored(new Vector4(0.98f, 0.5f, 0.46f, 1f), text);
                        break;
                    }
                    if (got == RynthKeyCapture.Cancelled) { _keySlot = 0; ImGui.CloseCurrentPopup(); break; }
                    _keyName = text;
                    RynthKeyCheck check = _host.CheckActionKey(KeyAction(_keySlot), _keyName, out string msg);
                    if (check == RynthKeyCheck.Free) Bind();
                    else
                    {
                        _keyStep = check == RynthKeyCheck.Taken ? KeyStep.Confirm : KeyStep.Message;
                        _keyText = msg;
                    }
                    break;
                case KeyStep.Confirm:
                    SafeImGui.Text(_keyText);
                    ImGui.TextDisabled($"Use it for slot {_keySlot} instead? It comes off those.");
                    if (ImGui.Button($"Use {_keyName} here")) Bind();
                    ImGui.SameLine();
                    if (ImGui.Button("Another key")) StartKeyCapture(_keySlot);
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel")) { _keySlot = 0; ImGui.CloseCurrentPopup(); }
                    break;
                case KeyStep.Message:
                    SafeImGui.Text(_keyText);
                    if (ImGui.Button("Another key")) StartKeyCapture(_keySlot);
                    ImGui.SameLine();
                    if (ImGui.Button("Close")) { _keySlot = 0; ImGui.CloseCurrentPopup(); }
                    break;
            }
            ImGui.PopTextWrapPos();
        }
        finally { ImGui.EndPopup(); }
    }

    private void Bind()
    {
        if (_host.BindActionKey(KeyAction(_keySlot), _keyName, out string why))
        {
            _act.KeysChanged();
            _keySlot = 0;
            ImGui.CloseCurrentPopup();
            return;
        }
        _keyStep = KeyStep.Message;
        _keyText = why;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static readonly System.Collections.Generic.Dictionary<(string, int), string> Ids = new();

    private static string Id(string prefix, int n)
    {
        if (!Ids.TryGetValue((prefix, n), out string? id)) Ids[(prefix, n)] = id = prefix + n;
        return id;
    }

    private static string Abbrev(string name)
    {
        if (name.Length == 0) return "?";
        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2) return string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[^1][0]));
        return name.Length <= 3 ? name : name[..3];
    }

    private static readonly string[] Chars = BuildChars();

    private static string[] BuildChars()
    {
        var a = new string[128];
        for (int i = 32; i < 127; i++) a[i] = ((char)i).ToString();
        return a;
    }

    /// <summary>Text with <paramref name="spacing"/> pixels between its letters. Returns the x after it.</summary>
    private static float Tracked(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, uint color, string text, float spacing)
    {
        unsafe { if (font.NativePtr == null || text.Length == 0) return pos.X; }
        float scale = size / font.FontSize;
        float x = pos.X;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            string s = c < 128 && Chars[c] != null ? Chars[c] : c.ToString();
            dl.AddText(font, size, new Vector2(MathF.Round(x), pos.Y), color, s);
            x += font.GetCharAdvance(c) * scale;
            if (i < text.Length - 1) x += spacing;
        }
        return x;
    }

    /// <summary>A Phosphor glyph centred in the box, <paramref name="px"/> across its em (from Ui18's bake).</summary>
    private static unsafe void DrawIcon(ImDrawListPtr dl, string glyph, Vector2 min, Vector2 size, float px, uint color)
    {
        if (string.IsNullOrEmpty(glyph)) return;
        ImFontPtr font = Font(RynthUiFont.Ui18);
        if (font.NativePtr == null) return;
        byte* g = (byte*)ImGuiNative.ImFont_FindGlyphNoFallback(font.NativePtr, glyph[0]);
        if (g == null) return;
        float advance = *(float*)(g + 4);
        if (advance <= 0) return;
        float* box = (float*)(g + 8);
        float scale = px / advance;
        Vector2 centre = new((box[0] + box[2]) * 0.5f * scale, (box[1] + box[3]) * 0.5f * scale);
        Vector2 at = min + size * 0.5f - centre;
        dl.AddText(font, font.FontSize * scale, new Vector2(MathF.Round(at.X), MathF.Round(at.Y)), color, glyph);
    }
}
