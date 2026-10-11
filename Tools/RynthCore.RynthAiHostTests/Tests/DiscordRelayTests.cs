using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthAi.Discord;
using RynthCore.Plugin.RynthAi.Translate;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Discord chat relay (0.5.33): copies chosen chat channels to a Discord channel by webhook or bot.
// These cover the pure parts: credential checks, Markdown / mention safety, message packing, routing
// of chat lines, rate-limit parsing, settings round-trip and DPAPI secret storage. No network calls.
internal static class DiscordRelayTests
{
    // Fake, assembled at runtime so no webhook-shaped literal in source trips secret scanning.
    private static readonly string GoodWebhook =
        "https://discord.com/api/webhooks/" + new string('1', 18) + "/" + new string('x', 68);

    public static void Register(Runner r)
    {
        r.Add("discord: webhook URL accepts Discord hosts only", WebhookUrl);
        r.Add("discord: bot token and channel ID shapes", BotShapes);
        r.Add("discord: markdown and mentions are neutralised", EscapeAndMentions);
        r.Add("discord: line format and truncation", LineFormat);
        r.Add("discord: packing stays under 2000 characters", Packing);
        r.Add("discord: chat lines classify by channel; echoes skipped", Classify);
        r.Add("discord: keyword and character filters", Filters);
        r.Add("discord: webhook display name rules", DisplayName);
        r.Add("discord: rate limit wait and permanent errors", RateLimit);
        r.Add("discord: request body blocks mentions and embeds", Body);
        r.Add("discord: settings round-trip keeps secrets encrypted", SettingsRoundTrip);
    }

    private static void WebhookUrl()
    {
        Check.True(DiscordFormat.TryNormalizeWebhookUrl(GoodWebhook, out string n), "discord.com webhook accepted");
        Check.Eq(n, GoodWebhook, "normalised URL unchanged");
        Check.True(DiscordFormat.TryNormalizeWebhookUrl(GoodWebhook + "?wait=true", out n) && n == GoodWebhook, "query string removed");
        Check.True(DiscordFormat.TryNormalizeWebhookUrl(GoodWebhook + "/", out n) && n == GoodWebhook, "trailing slash removed");
        Check.True(DiscordFormat.TryNormalizeWebhookUrl(GoodWebhook.Replace("discord.com", "canary.discord.com"), out _), "canary host accepted");
        Check.True(DiscordFormat.TryNormalizeWebhookUrl(GoodWebhook.Replace("discord.com", "discordapp.com"), out _), "discordapp.com accepted");
        Check.True(DiscordFormat.TryNormalizeWebhookUrl(GoodWebhook.Replace("/api/", "/api/v10/"), out _), "versioned path accepted");
        Check.False(DiscordFormat.TryNormalizeWebhookUrl(GoodWebhook.Replace("https://", "http://"), out _), "plain http refused");
        Check.False(DiscordFormat.TryNormalizeWebhookUrl(GoodWebhook.Replace("discord.com", "discord.com.evil.example"), out _), "look-alike host refused");
        Check.False(DiscordFormat.TryNormalizeWebhookUrl("https://evil.example/api/webhooks/123456789012345678/abc", out _), "other host refused");
        Check.False(DiscordFormat.TryNormalizeWebhookUrl("", out _), "blank refused");
    }

    private static void BotShapes()
    {
        Check.True(DiscordFormat.IsSnowflake("123456789012345678"), "18-digit channel ID");
        Check.False(DiscordFormat.IsSnowflake("12345"), "too short");
        Check.False(DiscordFormat.IsSnowflake("12345678901234567a"), "letters refused");
        // Built at runtime: a token-shaped literal in source trips secret scanning (GitHub push protection).
        string fakeToken = string.Join(".", new string('A', 24), new string('b', 6), new string('c', 38));
        Check.True(DiscordFormat.LooksLikeBotToken(fakeToken), "three-part token");
        Check.False(DiscordFormat.LooksLikeBotToken("not a token"), "free text refused");
        Check.False(DiscordFormat.LooksLikeBotToken(GoodWebhook), "webhook URL is not a bot token");
    }

    private static void EscapeAndMentions()
    {
        Check.Eq(DiscordFormat.Escape("**bold** _it_ ~~s~~ `c` ||sp||"),
            "\\*\\*bold\\*\\* \\_it\\_ \\~\\~s\\~\\~ \\`c\\` \\|\\|sp\\|\\|", "inline markdown escaped");
        Check.Eq(DiscordFormat.Escape("[x](http://a)"), "\\[x\\](http://a)", "masked link escaped");
        Check.Eq(DiscordFormat.Escape("<@123> > quote"), "\\<@123\\> \\> quote", "mention tag and quote escaped");
        Check.Eq(DiscordFormat.Escape("hi @everyone and @here"), "hi @\u200Beveryone and @\u200Bhere", "mass mentions defused");
        Check.Eq(DiscordFormat.Escape("a\r\nb"), "a  b", "newlines flattened");
        Check.Eq(DiscordFormat.Escape(null), "", "null is empty");
    }

    private static void LineFormat()
    {
        var t = new DateTime(2026, 10, 11, 9, 5, 7);
        Check.Eq(DiscordFormat.FormatLine(t, "Allegiance", "Bob", "hello *all*", true, null),
            "`09:05:07` **\\[Allegiance\\] Bob:** hello \\*all\\*", "channel line with timestamp");
        Check.Eq(DiscordFormat.FormatLine(t, "Tell", "Bob", "hi", false, "Kelpie"),
            "\\[Kelpie\\] **\\[Tell\\] Bob:** hi", "character tag, no timestamp");
        Check.Eq(DiscordFormat.FormatLine(t, null, null, "# Rare found", false, null), "\\# Rare found", "heading at line start escaped");
        Check.Eq(DiscordFormat.FormatLine(t, null, null, "- item", false, null), "\\- item", "list at line start escaped");
        Check.Eq(DiscordFormat.FormatLine(t, null, null, "x # y", false, null), "x # y", "mid-line # left alone");
        string longLine = DiscordFormat.FormatLine(t, null, null, new string('a', 5000), false, null);
        Check.Eq(longLine.Length, DiscordFormat.MaxLine, "long line cut to MaxLine");
        Check.True(longLine.EndsWith("\u2026"), "cut line ends with an ellipsis");
        Check.Eq(DiscordFormat.Truncate("abc\\*def", 5), "abc\u2026", "cut never leaves a dangling backslash");
    }

    private static void Packing()
    {
        var lines = new List<string>();
        for (int i = 0; i < 30; i++) lines.Add(new string((char)('a' + i % 26), 150));
        string first = DiscordFormat.Pack(lines, out int taken);
        Check.True(first.Length <= DiscordFormat.MaxContent, "first message within the limit");
        Check.Eq(taken, 13, "13 lines of 150 (+ newline) fit in 2000");
        Check.Eq(first.Split('\n').Length, taken, "one line per taken entry");

        var big = new List<string> { new string('x', 2500), "next" };
        string one = DiscordFormat.Pack(big, out taken);
        Check.Eq(taken, 1, "an oversize line still goes, alone");
        Check.Eq(one.Length, DiscordFormat.MaxContent, "and is cut to the content limit");

        DiscordFormat.Pack(new List<string>(), out taken);
        Check.Eq(taken, 0, "nothing to pack");
    }

    private static void Classify()
    {
        Check.True(DiscordRelay.TryClassify("Bob says to your allegiance, \"inc at the portal\"", 0x12, "Kelpie",
            out var ch, out string who, out string msg, out bool own), "allegiance speech parses");
        Check.Eq(ch, (TranslateChannel?)TranslateChannel.Allegiance, "allegiance channel");
        Check.Eq(who, "Bob", "speaker");
        Check.Eq(msg, "inc at the portal", "message");
        Check.False(own, "not own");

        Check.True(DiscordRelay.TryClassify("Bob tells you, \"hi\"", 0x03, "Kelpie", out ch, out who, out _, out _), "tell parses");
        Check.Eq(ch, (TranslateChannel?)TranslateChannel.Tell, "tell channel");

        Check.True(DiscordRelay.TryClassify("You say, \"hello\"", 0x02, "Kelpie", out ch, out who, out _, out own), "own speech parses");
        Check.True(own, "own flagged");
        Check.Eq(who, "Kelpie", "own speaker shown as the character name");

        Check.True(DiscordRelay.TryClassify("Bob has discovered the Pearl of Blood Drinking!", 0x01, "Kelpie",
            out ch, out who, out msg, out _), "system line is Other");
        Check.Eq(ch, (TranslateChannel?)null, "Other has no channel");
        Check.Eq(who, "", "Other has no speaker");

        Check.False(DiscordRelay.TryClassify("[Translate] [General] Bob (es): hi", 0x1B, "Kelpie", out _, out _, out _, out _), "translator echo skipped");
        Check.False(DiscordRelay.TryClassify("[Discord] Relay on.", 0x01, "Kelpie", out _, out _, out _, out _), "relay's own line skipped");
        Check.False(DiscordRelay.TryClassify("Zojak Bor", 0x11, "Kelpie", out _, out _, out _, out _), "spell words skipped");
        Check.False(DiscordRelay.TryClassify("   ", 0x01, "Kelpie", out _, out _, out _, out _), "blank skipped");
    }

    private static void Filters()
    {
        var kw = DiscordFormat.SplitList(" rare , Asheron ,, ");
        Check.Eq(kw.Count, 2, "blank entries dropped");
        Check.True(DiscordFormat.MatchesKeywords("Bob found a RARE gem", kw), "keyword match ignores case");
        Check.False(DiscordFormat.MatchesKeywords("nothing here", kw), "no keyword, no relay");
        Check.True(DiscordFormat.MatchesKeywords("anything", new List<string>()), "no keywords relays everything");

        Check.True(DiscordFormat.CharacterAllowed("", "Kelpie"), "blank list allows every character");
        Check.True(DiscordFormat.CharacterAllowed("Bob, kelpie", "+Kelpie"), "listed (case and + ignored)");
        Check.False(DiscordFormat.CharacterAllowed("Bob", "Kelpie"), "unlisted character blocked");
        Check.False(DiscordFormat.CharacterAllowed("Bob", ""), "unknown character blocked when a list is set");
    }

    private static void DisplayName()
    {
        Check.Eq(DiscordFormat.SafeWebhookName("+Kelpie"), "Kelpie", "admin + removed");
        Check.Eq(DiscordFormat.SafeWebhookName("My Discord Bot"), "", "names with 'discord' fall back");
        Check.Eq(DiscordFormat.SafeWebhookName("Clyde"), "", "'clyde' falls back");
        Check.Eq(DiscordFormat.SafeWebhookName("everyone"), "", "'everyone' falls back");
        Check.Eq(DiscordFormat.SafeWebhookName(new string('n', 100)).Length, 80, "cut to 80");
    }

    private static void RateLimit()
    {
        Check.Eq(DiscordClient.ParseRetryAfterMs("{\"message\":\"You are being rate limited.\",\"retry_after\":1.5,\"global\":false}", null), 1500, "JSON retry_after in seconds");
        Check.Eq(DiscordClient.ParseRetryAfterMs("", "3"), 3000, "Retry-After header fallback");
        Check.Eq(DiscordClient.ParseRetryAfterMs("not json", null), 5000, "default 5 s");
        Check.Eq(DiscordClient.ParseRetryAfterMs("{\"retry_after\":0.01}", null), 250, "floor 250 ms");
        Check.Eq(DiscordClient.ParseRetryAfterMs("{\"retry_after\":99999}", null), 600_000, "cap 10 min");
        foreach (int s in new[] { 400, 401, 403, 404 }) Check.True(DiscordClient.IsPermanent(s), $"{s} pauses the route");
        foreach (int s in new[] { 429, 500, 502, 503 }) Check.False(DiscordClient.IsPermanent(s), $"{s} is retried");
    }

    private static void Body()
    {
        string json = DiscordClient.BuildBody("hi \"there\"", "Kelpie");
        Check.True(json.Contains("\"allowed_mentions\":{\"parse\":[]}"), "no mentions parsed");
        Check.True(json.Contains("\"flags\":4"), "embeds suppressed");
        Check.True(json.Contains("\"username\":\"Kelpie\""), "webhook username set");
        Check.True(json.Contains("\\u0022there\\u0022"), "content JSON-escaped");
        Check.False(DiscordClient.BuildBody("x", "").Contains("username"), "no username when blank");
    }

    private static void SettingsRoundTrip()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rynthai-discord-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "discord.json");
        try
        {
            var store = new DiscordRelayStore(path);
            var s = store.Load();
            Check.Eq(s.Routes.Length, 0, "new file has no routes");

            var relay = new DiscordRelay(() => "Kelpie", store);
            var route = new DiscordRoute { Name = "Alleg" };
            DiscordRelayStore.NormalizeRoute(route);
            Check.Eq(route.ChannelMask, DiscordRoute.DefaultChannelMask(), "new route gets the default channels");
            relay.Settings.Routes = new[] { route };
            Check.True(relay.SetWebhook(route, "https://evil.example/x").Length > 0, "bad URL rejected");
            Check.Eq(route.WebhookProtected, "", "nothing saved for a bad URL");
            Check.Eq(relay.SetWebhook(route, GoodWebhook), "", "good URL saved");
            Check.True(route.HasSecret, "route has a destination");
            route.ChannelMask = TranslateChannels.Bit(TranslateChannel.General) | TranslateChannels.Bit(TranslateChannel.Tell);
            relay.Dispose();

            string raw = File.ReadAllText(path);
            Check.False(raw.Contains("discord.com/api/webhooks"), "webhook URL not stored in plain text");
            Check.False(raw.Contains("ChannelMask"), "runtime mask not stored");
            Check.True(raw.Contains("General") && raw.Contains("Tell"), "channels stored by name");

            var back = new DiscordRelayStore(path).Load();
            Check.Eq(back.Routes.Length, 1, "route reloaded");
            var r2 = back.Routes[0];
            Check.True(r2.RelaysChannel(TranslateChannel.General) && r2.RelaysChannel(TranslateChannel.Tell), "channels reloaded");
            Check.False(r2.RelaysChannel(TranslateChannel.Allegiance), "unticked channel stays off");
            Check.True(DiscordSecrets.TryUnprotect(r2.WebhookProtected, out string url) && url == GoodWebhook, "secret decrypts for this user");
            Check.False(DiscordSecrets.TryUnprotect("bm90IGEgYmxvYg==", out _), "foreign blob does not decrypt");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
