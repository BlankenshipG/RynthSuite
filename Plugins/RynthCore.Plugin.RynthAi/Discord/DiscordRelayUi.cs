// DiscordRelayUi.cs — "Discord Relay" ImGui window. Render thread only.
//
//   Routes    one tab per destination: webhook URL or bot token + channel ID, chat channels, keywords
//   Log       messages sent, rate limits and errors
//   Settings  character filter, timestamps, batching and spacing
//
// Settings fields are edited in place (single-value writes the pump thread tolerates). Adding or
// removing a route replaces the Routes array. Secrets go straight to DPAPI through DiscordRelay and are
// never shown again; the input box is cleared after saving.
using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.Translate;

namespace RynthCore.Plugin.RynthAi.Discord;

internal sealed class DiscordRelayUi
{
    private static readonly Vector4 ColHead = new(0.45f, 0.85f, 0.95f, 1f);
    private static readonly Vector4 ColOk = new(0.40f, 0.95f, 0.45f, 1f);
    private static readonly Vector4 ColWarn = new(0.95f, 0.80f, 0.35f, 1f);
    private static readonly Vector4 ColError = new(0.95f, 0.45f, 0.40f, 1f);
    private static readonly Vector4 ColDim = new(0.60f, 0.64f, 0.70f, 1f);

    private readonly DiscordRelay _relay;
    private string _secretInput = string.Empty;
    private string _secretMessage = string.Empty;
    private bool _secretMessageIsError;
    private DiscordRoute? _secretFor;
    private int _lastLogCount;

    public DiscordRelayUi(DiscordRelay relay) => _relay = relay;

    private DiscordRelaySettings S => _relay.Settings;

    public void RenderWindow()
    {
        var s = S;
        if (!s.ShowWindow) return;

        ImGui.SetNextWindowSize(new Vector2(560, 480), ImGuiCond.FirstUseEver);
        bool open = true;
        bool visible = ImGui.Begin("Discord Relay##rynthdiscord", ref open, ImGuiWindowFlags.NoCollapse);
        if (!open) s.ShowWindow = false;
        if (!visible) { ImGui.End(); return; }

        ImGui.Checkbox("Relay enabled##dcenabled", ref s.Enabled);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copy the chosen chat channels to Discord. Off: nothing is queued or sent.");
        ImGui.SameLine();
        ImGui.TextColored(ColDim, "| " + _relay.StatusLine());
        if (!DiscordFormat.CharacterAllowed(s.OnlyCharacters, CurrentNameHint))
            ImGui.TextColored(ColWarn, "This character isn't in \"Only these characters\" (Settings tab), so it relays nothing.");
        ImGui.Separator();

        if (ImGui.BeginTabBar("##discordtabs"))
        {
            if (ImGui.BeginTabItem("Routes")) { RenderRoutes(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Log")) { RenderLog(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Settings")) { RenderSettings(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    /// <summary>Character name for the window's warning line; set by the plugin each login.</summary>
    public string CurrentNameHint { get; set; } = string.Empty;

    // ── Routes ─────────────────────────────────────────────────────────────

    private void RenderRoutes()
    {
        var s = S;
        if (ImGui.SmallButton("Add route"))
        {
            var r = new DiscordRoute { Name = $"Route {s.Routes.Length + 1}" };
            DiscordRelayStore.NormalizeRoute(r);
            var list = new List<DiscordRoute>(s.Routes) { r };
            s.Routes = list.ToArray();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("A route is one Discord channel and the chat it receives.");

        if (s.Routes.Length == 0)
        {
            ImGui.Spacing();
            ImGui.TextWrapped("No routes yet. Add one, then paste a webhook URL from Discord: "
                              + "Server Settings > Integrations > Webhooks > New Webhook > pick the channel > Copy Webhook URL.");
            return;
        }

        if (!ImGui.BeginTabBar("##discordroutes", ImGuiTabBarFlags.FittingPolicyScroll)) return;
        DiscordRoute? remove = null;
        for (int i = 0; i < s.Routes.Length; i++)
        {
            var r = s.Routes[i];
            var st = _relay.StatusOf(r);
            string label = (st?.Paused == true ? "! " : string.Empty) + r.Name + "###route" + i;
            if (!ImGui.BeginTabItem(label)) continue;
            if (RenderRoute(r, i)) remove = r;
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();

        if (remove != null)
        {
            var list = new List<DiscordRoute>(s.Routes);
            list.Remove(remove);
            s.Routes = list.ToArray();
            if (ReferenceEquals(_secretFor, remove)) ClearSecretBox();
        }
    }

    /// <summary>Draws one route's editor; true when the user asked to remove it.</summary>
    private bool RenderRoute(DiscordRoute r, int index)
    {
        string id = "##r" + index;
        var st = _relay.StatusOf(r);
        if (!ReferenceEquals(_secretFor, r)) ClearSecretBox(r);

        // Status line
        if (st != null)
        {
            if (st.Paused) ImGui.TextColored(ColError, "Paused: " + st.LastError);
            else if (st.LastError.Length > 0) ImGui.TextColored(ColWarn, "Last problem: " + st.LastError);
            ImGui.TextColored(ColDim, $"{st.MessagesSent} message(s), {st.LinesSent} line(s) sent, {st.Queued} queued, {st.Dropped} dropped"
                                      + (st.LastSent != default ? $", last {st.LastSent:HH:mm:ss}" : string.Empty));
        }

        ImGui.Checkbox("Enabled" + id, ref r.Enabled);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f);
        string name = r.Name;
        if (ImGui.InputText("Name" + id, ref name, 40u)) r.Name = name;
        if (ImGui.IsItemDeactivatedAfterEdit() && string.IsNullOrWhiteSpace(r.Name)) r.Name = "Discord";

        // Destination
        ImGui.Spacing();
        ImGui.TextColored(ColHead, "Destination");
        int mode = Math.Max(0, Array.IndexOf(DiscordRoute.Modes, r.Mode));
        ImGui.SetNextItemWidth(220f);
        if (ImGui.Combo("Send with" + id, ref mode, DiscordRoute.ModeNames, DiscordRoute.ModeNames.Length))
        {
            r.Mode = DiscordRoute.Modes[mode];
            ClearSecretBox(r);
            _relay.Resume(r);
        }

        bool bot = r.Mode == DiscordRoute.ModeBot;
        bool saved = bot ? r.BotTokenProtected.Length > 0 : r.WebhookProtected.Length > 0;
        ImGui.SetNextItemWidth(330f);
        ImGui.InputTextWithHint((bot ? "Bot token" : "Webhook URL") + id,
            saved ? "saved (paste a new one to replace it)" : bot ? "paste the bot token" : "https://discord.com/api/webhooks/...",
            ref _secretInput, 512u, ImGuiInputTextFlags.Password);
        ImGui.SameLine();
        if (ImGui.Button("Save" + id) && _secretInput.Trim().Length > 0)
        {
            string err = bot ? _relay.SetBotToken(r, _secretInput) : _relay.SetWebhook(r, _secretInput);
            _secretInput = string.Empty;
            _secretMessageIsError = err.Length > 0;
            _secretMessage = err.Length > 0 ? err : "Saved (encrypted for this Windows user). Use Send test to check it.";
        }
        if (saved)
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear" + id))
            {
                if (bot) r.BotTokenProtected = string.Empty; else r.WebhookProtected = string.Empty;
                _secretMessage = "Cleared.";
                _secretMessageIsError = false;
            }
        }
        if (_secretMessage.Length > 0) ImGui.TextColored(_secretMessageIsError ? ColError : ColOk, _secretMessage);

        if (bot)
        {
            ImGui.SetNextItemWidth(200f);
            string chId = r.ChannelId;
            if (ImGui.InputTextWithHint("Channel ID" + id, "e.g. 123456789012345678", ref chId, 24u, ImGuiInputTextFlags.CharsDecimal))
            {
                r.ChannelId = chId.Trim();
                _relay.Resume(r);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Discord: User Settings > Advanced > Developer Mode on, then right-click the channel > Copy Channel ID.\n"
                                 + "The bot must be in the server with View Channel and Send Messages there.");
            if (r.ChannelId.Length > 0 && !DiscordFormat.IsSnowflake(r.ChannelId))
                ImGui.TextColored(ColError, "A channel ID is 15-22 digits.");
        }
        else
        {
            ImGui.SetNextItemWidth(200f);
            ImGui.InputTextWithHint("Post as" + id, "character name", ref r.DisplayName, 80u);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Name shown on the webhook's messages. Blank uses the character's name.\n"
                                 + "Discord rejects names containing \"discord\" or \"clyde\"; those fall back to the webhook's own name.");
        }

        if (ImGui.Button("Send test" + id)) _relay.SendTest(r);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Posts one test line to this route now (also clears a pause).");
        if (st?.Paused == true)
        {
            ImGui.SameLine();
            if (ImGui.Button("Resume" + id)) _relay.Resume(r);
        }

        // Chat channels
        ImGui.Spacing();
        ImGui.TextColored(ColHead, "Chat to relay");
        if (ImGui.SmallButton("All" + id)) r.ChannelMask = AllMask();
        ImGui.SameLine();
        if (ImGui.SmallButton("None" + id)) r.ChannelMask = 0;
        ImGui.SameLine();
        if (ImGui.SmallButton("Defaults" + id)) r.ChannelMask = DiscordRoute.DefaultChannelMask();
        if (ImGui.BeginTable("##chgrid" + index, 4, ImGuiTableFlags.SizingStretchSame))
        {
            foreach (var ch in TranslateChannels.All)
            {
                ImGui.TableNextColumn();
                uint bit = TranslateChannels.Bit(ch);
                bool on = (r.ChannelMask & bit) != 0;
                if (ImGui.Checkbox(TranslateChannels.Name(ch) + id + (int)ch, ref on))
                    r.ChannelMask = on ? r.ChannelMask | bit : r.ChannelMask & ~bit;
                if (ch == TranslateChannel.Local && ImGui.IsItemHovered())
                    ImGui.SetTooltip("Local speech includes every NPC and player near you; it can be busy.");
            }
            ImGui.TableNextColumn();
            ImGui.Checkbox("Other" + id, ref r.IncludeOther);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Lines that aren't player speech: system and server broadcasts, combat, loot.\n"
                                 + "Very busy; pair it with keywords below.");
            ImGui.EndTable();
        }

        ImGui.SetNextItemWidth(330f);
        ImGui.InputTextWithHint("Keywords" + id, "blank = every line; e.g. rare, Asheron", ref r.Keywords, 300u);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Comma-separated. When set, only lines containing one of these words (any case) are relayed.");

        ImGui.Spacing();
        ImGui.Separator();
        bool remove = ImGui.SmallButton("Remove route" + id);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Deletes this route and its saved credential.");
        return remove;
    }

    private void ClearSecretBox(DiscordRoute? forRoute = null)
    {
        _secretFor = forRoute;
        _secretInput = string.Empty;
        _secretMessage = string.Empty;
        _secretMessageIsError = false;
    }

    private static uint AllMask()
    {
        uint m = 0;
        foreach (var ch in TranslateChannels.All) m |= TranslateChannels.Bit(ch);
        return m;
    }

    // ── Log ────────────────────────────────────────────────────────────────

    private void RenderLog()
    {
        var log = _relay.LogSnapshot();
        if (ImGui.SmallButton("Clear")) _relay.ClearLog();
        ImGui.SameLine();
        ImGui.TextColored(ColDim, $"{log.Length} entries");

        if (!ImGui.BeginChild("##dclog", new Vector2(0, 0), ImGuiChildFlags.Borders)) { ImGui.EndChild(); return; }
        foreach (var e in log)
        {
            var col = e.Kind switch { DiscordLogKind.Sent => ColOk, DiscordLogKind.Error => ColError, _ => ColWarn };
            ImGui.TextColored(ColDim, e.Time.ToString("HH:mm:ss"));
            ImGui.SameLine();
            ImGui.TextColored(col, e.Route);
            ImGui.SameLine();
            ImGui.TextWrapped(e.Text);
        }
        // Follow new lines unless the user scrolled up.
        if (log.Length != _lastLogCount && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20f)
            ImGui.SetScrollHereY(1f);
        _lastLogCount = log.Length;
        ImGui.EndChild();
    }

    // ── Settings ───────────────────────────────────────────────────────────

    private void RenderSettings()
    {
        var s = S;
        ImGui.TextColored(ColHead, "Who relays");
        ImGui.SetNextItemWidth(300f);
        ImGui.InputTextWithHint("Only these characters##dconly", "blank = every character", ref s.OnlyCharacters, 400u);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Comma-separated character names. With several clients in one allegiance or fellowship,\n"
                             + "list just one of them so each line is posted once.");
        ImGui.Checkbox("Skip lines I type##dcskipown", ref s.SkipOwn);

        ImGui.Spacing();
        ImGui.TextColored(ColHead, "Message format");
        ImGui.Checkbox("Timestamp (HH:mm:ss)##dcts", ref s.IncludeTimestamp);
        ImGui.Checkbox("Start lines with [Character]##dctag", ref s.TagCharacter);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Useful when several characters post into the same Discord channel.");

        ImGui.Spacing();
        ImGui.TextColored(ColHead, "Pacing");
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("Batch window (ms)##dcbatch", ref s.BatchMs, 0, 5000)) s.BatchMs = Math.Clamp(s.BatchMs, 0, 10000);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Lines arriving this close together go out in one Discord message.");
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("Min ms between messages##dcspace", ref s.MinIntervalMs, 1000, 10000)) s.MinIntervalMs = Math.Clamp(s.MinIntervalMs, 1000, 60000);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Per route. Discord allows about 30 messages a minute per channel; 2000 ms stays under it.");
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("Queue limit (lines)##dcqueue", ref s.MaxQueue, 20, 1000)) s.MaxQueue = Math.Clamp(s.MaxQueue, 20, 2000);
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("Log size##dclog", ref s.MaxLog, 20, 1000)) s.MaxLog = Math.Clamp(s.MaxLog, 20, 2000);

        ImGui.Spacing();
        ImGui.TextColored(ColDim, "Shared by all characters (RynthAi\\discord.json). Webhook URLs and bot tokens are encrypted");
        ImGui.TextColored(ColDim, "for this Windows user; copied to another PC they must be pasted again.");
        ImGui.TextColored(ColDim, "Relayed text never pings: @everyone, @here and user mentions are sent as plain text.");
    }
}
