// DiscordRelaySettings.cs — Discord chat relay settings, shared by every character (RynthAi\discord.json).
//
// Fields are written by the render thread (UI) and read by the pump thread; each is a single reference
// or 32-bit value so reads never tear. Routes is replaced, never mutated in place, when a route is added
// or removed, so the pump can iterate the array it read without a lock.
//
// Secrets (webhook URL, bot token) are stored only as DPAPI blobs (DiscordSecrets); the JSON never holds
// them in plain text.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RynthCore.Install;
using RynthCore.Plugin.RynthAi.Translate;

namespace RynthCore.Plugin.RynthAi.Discord;

/// <summary>One Discord destination and the chat it receives.</summary>
internal sealed class DiscordRoute
{
    public const string ModeWebhook = "webhook";
    public const string ModeBot = "bot";
    public static readonly string[] Modes = { ModeWebhook, ModeBot };
    public static readonly string[] ModeNames = { "Webhook URL", "Bot token + channel ID" };

    /// <summary>Label shown in the window and in /ra discord output.</summary>
    public string Name = "Discord";

    public bool Enabled = true;

    public string Mode = ModeWebhook;

    /// <summary>DPAPI blob of the webhook URL (the URL embeds the webhook token).</summary>
    public string WebhookProtected = string.Empty;

    /// <summary>DPAPI blob of the bot token (bot mode).</summary>
    public string BotTokenProtected = string.Empty;

    /// <summary>Target channel snowflake (bot mode). Not secret.</summary>
    public string ChannelId = string.Empty;

    /// <summary>Webhook display name; blank uses the character name. Ignored in bot mode.</summary>
    public string DisplayName = string.Empty;

    /// <summary>Chat channels relayed to this route (names; see <see cref="TranslateChannels.TryParse"/>).</summary>
    public List<string>? Channels;

    /// <summary>Also relay lines that aren't player speech (system broadcasts, server messages).</summary>
    public bool IncludeOther;

    /// <summary>Comma-separated words; when set, only lines containing one of them are relayed.</summary>
    public string Keywords = string.Empty;

    // ── Runtime only ────────────────────────────────────────────────────────

    [JsonIgnore] public uint ChannelMask;

    /// <summary>True when a send secret is stored for the current mode (the plain text is never kept here).</summary>
    [JsonIgnore]
    public bool HasSecret => Mode == ModeBot
        ? BotTokenProtected.Length > 0 && ChannelId.Length > 0
        : WebhookProtected.Length > 0;

    public bool RelaysChannel(TranslateChannel ch) => (ChannelMask & TranslateChannels.Bit(ch)) != 0;

    /// <summary>Allegiance, fellowship, tells and the global chat channels.</summary>
    public static uint DefaultChannelMask()
    {
        uint m = 0;
        foreach (var ch in new[]
                 {
                     TranslateChannel.Tell, TranslateChannel.Fellowship, TranslateChannel.Allegiance,
                     TranslateChannel.Patron, TranslateChannel.Vassals, TranslateChannel.Covassals,
                     TranslateChannel.Monarch,
                 })
            m |= TranslateChannels.Bit(ch);
        return m;
    }
}

internal sealed class DiscordRelaySettings
{
    /// <summary>Master switch; off means nothing is queued or sent.</summary>
    public bool Enabled;

    /// <summary>Destinations. Replaced (copy-on-write) by the UI when one is added or removed.</summary>
    public DiscordRoute[] Routes = Array.Empty<DiscordRoute>();

    /// <summary>
    /// Comma-separated character names allowed to relay; blank = every character. With several clients
    /// in the same allegiance or fellowship, set this so only one of them posts each line.
    /// </summary>
    public string OnlyCharacters = string.Empty;

    /// <summary>Leave out lines you typed yourself ("You say", "You tell").</summary>
    public bool SkipOwn;

    /// <summary>Start each relayed line with the game time (HH:mm:ss).</summary>
    public bool IncludeTimestamp = true;

    /// <summary>Start each relayed line with "[Character]" (useful when several characters share a channel).</summary>
    public bool TagCharacter;

    /// <summary>Lines arriving within this window go out in one Discord message.</summary>
    public int BatchMs = 1500;

    /// <summary>Minimum spacing between messages to one route (Discord allows about 30 per minute per channel).</summary>
    public int MinIntervalMs = 2000;

    /// <summary>Lines waiting per route; the oldest are dropped beyond this.</summary>
    public int MaxQueue = 200;

    /// <summary>Entries kept in the window log.</summary>
    public int MaxLog = 200;

    public bool ShowWindow;

    public static List<string> NamesFromMask(uint mask) => TranslateSettings.NamesFromMask(mask);

    public static uint MaskFromNames(List<string>? names, uint fallback) => TranslateSettings.MaskFromNames(names, fallback);
}

/// <summary>Loads / saves <see cref="DiscordRelaySettings"/>; writes only when the JSON changed.</summary>
internal sealed class DiscordRelayStore
{
    private readonly string _path;
    private string _lastJson = string.Empty;

    public DiscordRelayStore(string? path = null)
        => _path = path ?? Path.Combine(RynthInstallPaths.RynthAiDir, "discord.json");

    public string FilePath => _path;

    public DiscordRelaySettings Load()
    {
        DiscordRelaySettings? s = null;
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                s = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.DiscordRelaySettings);
                if (s != null) _lastJson = json;
            }
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.Chat, ex, $"discord load {_path}"); }
        s ??= new DiscordRelaySettings();
        Normalize(s);
        return s;
    }

    public void SaveIfDirty(DiscordRelaySettings s)
    {
        try
        {
            foreach (var r in s.Routes) r.Channels = DiscordRelaySettings.NamesFromMask(r.ChannelMask);
            string json = JsonSerializer.Serialize(s, RynthAiJsonContext.Default.DiscordRelaySettings);
            if (json == _lastJson) return;
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
            _lastJson = json;
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.Chat, ex, $"discord save {_path}"); }
    }

    /// <summary>Repairs missing / out-of-range values and builds the runtime channel masks.</summary>
    internal static void Normalize(DiscordRelaySettings s)
    {
        s.Routes ??= Array.Empty<DiscordRoute>();
        var routes = new List<DiscordRoute>(s.Routes.Length);
        foreach (var r in s.Routes)
        {
            if (r == null) continue;
            NormalizeRoute(r);
            routes.Add(r);
        }
        s.Routes = routes.ToArray();
        s.OnlyCharacters ??= string.Empty;
        s.BatchMs = Math.Clamp(s.BatchMs, 0, 10000);
        s.MinIntervalMs = Math.Clamp(s.MinIntervalMs, 1000, 60000);
        s.MaxQueue = Math.Clamp(s.MaxQueue, 20, 2000);
        s.MaxLog = Math.Clamp(s.MaxLog, 20, 2000);
    }

    internal static void NormalizeRoute(DiscordRoute r)
    {
        r.Name = string.IsNullOrWhiteSpace(r.Name) ? "Discord" : r.Name.Trim();
        if (Array.IndexOf(DiscordRoute.Modes, r.Mode ?? string.Empty) < 0) r.Mode = DiscordRoute.ModeWebhook;
        r.WebhookProtected ??= string.Empty;
        r.BotTokenProtected ??= string.Empty;
        r.ChannelId = (r.ChannelId ?? string.Empty).Trim();
        r.DisplayName ??= string.Empty;
        r.Keywords ??= string.Empty;
        r.ChannelMask = DiscordRelaySettings.MaskFromNames(r.Channels, DiscordRoute.DefaultChannelMask());
    }
}
