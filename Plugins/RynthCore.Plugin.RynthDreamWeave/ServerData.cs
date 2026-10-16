// ServerData.cs - What is known about DreamWeave (RynthDreamWeave). Identity (id, hosts, world names) is in the csproj.
// The features and commands come from the server's public source (ACE-DreamWeave: its README and the
// player-level chat commands under Source/ACE.Server/Command/Handlers). Stock ACE commands and the
// stage-test-shard grants (/myxp, /mylum, ...) are left out.
namespace RynthCore.Plugin.ServerPack;

internal static class ServerData
{
    public static ServerPackInfo Create() =>
        new(RynthPluginInfo.ServerId, RynthPluginInfo.ServerName, RynthPluginInfo.ServerHosts, RynthPluginInfo.ServerWorldNames)
        {
            Port = 9000,
            Emulator = "ACE",
            Ruleset = "PvE",
            Status = "Development",
            Summary = "PvE server pushing unmodified end-of-retail ToD to the limit. Lore driven, new class abilities, " +
                      "new crafting system, tons of QOL, up-to-date wiki.",
            Facts = new[]
            {
                "Lore driven, built on end-of-retail Throne of Destiny.",
                "New class abilities and a new crafting system.",
                "Its wiki is kept up to date.",
                "Class abilities: trainers sell ability tokens for class ability points (/abilities, /cavoucher).",
                "Equipment and weapon mods on armor, clothing and jewelry (/mods).",
                "Facets: several builds per character, including a PK facet (/facet).",
                "In-game bank for pyreals, luminance, legendary keys, promissory and trade notes (/bank).",
                "Account vault (mule) with its own vendor, shared with characters you choose (/mule).",
                "Player market: browse, list and buy items (/market).",
                "Realms and ephemeral instances (/realm, /exitinst).",
                "Drift Network: a hall of linked drift-doors, recalled to with /dn (/tn for the retail Town Network).",
                "Threads dungeons (/dd) and treasure-map dig sites (/digsite, /rrtm).",
                "PvP arenas (1v1, 2v2, Tugak Brawl free-for-all) and battlegrounds on templates, with ratings (/arena).",
                "Leaderboards for DPS, defense, waves, speed, level, bank, stamps, threads and PvP (/top).",
                "XP, luminance and custom augmentations (/aug).",
                "Experience bonuses: gear, augmentations, alt-character catch-up and offline (/bonus).",
                "Fast tick opt-in: faster spell release, fast-chug and melee stick-to-target (/fasttick).",
                "Web character sheet you can publish (/charsheet).",
            },
            Commands = new[]
            {
                new ServerCommand("/bank", "deposit, withdraw, transfer and check banked currency (/b d deposits everything)"),
                new ServerCommand("/mule", "summon your account vault's vendor, or manage who can use it"),
                new ServerCommand("/market", "browse, list and buy items on the market"),
                new ServerCommand("/abilities", "manage your class abilities"),
                new ServerCommand("/cavoucher", "buy a class ability token for ability points, or refund an unused one"),
                new ServerCommand("/facet", "show your facets; /facet <N> switches build, /facet pk the PK facet"),
                new ServerCommand("/mods", "summarize the equipment mods on what you wear"),
                new ServerCommand("/aug", "augmentation list: /aug xp, /aug lum or /aug custom"),
                new ServerCommand("/dn", "recall to the Drift Network (moving too far interrupts it)"),
                new ServerCommand("/tn", "recall to the retail Town Network"),
                new ServerCommand("/realm", "which realm and instance you are in"),
                new ServerCommand("/exitinst", "leave the current ephemeral instance"),
                new ServerCommand("/dd", "Threads dungeon status"),
                new ServerCommand("/digsite", "status of the dig site you are at; /digsite bail ends one you dug"),
                new ServerCommand("/rrtm", "reroll a treasure map's dig site to the one nearest you"),
                new ServerCommand("/arena", "PvP arena: /arena join 1v1|2v2|tugak|bg, templates, status, rating, top"),
                new ServerCommand("/top", "leaderboards: dps, defense, wave, speed, level, bank, stamp, thread, 1v1, 2v2, tugak, bg"),
                new ServerCommand("/xp", "your experience progress"),
                new ServerCommand("/quests", "your quest stamp progress"),
                new ServerCommand("/bonus", "every experience bonus you have and the total (/altbonus, /offlinebonus)"),
                new ServerCommand("/lph", "luminance per hour since login, or since /lph start"),
                new ServerCommand("/pickupspeed", "your pick-up speed bonus, including permanent quest bonuses"),
                new ServerCommand("/fasttick", "fast tick on, off, default or status"),
                new ServerCommand("/dotdamage", "show or hide your damage-over-time tick messages"),
                new ServerCommand("/summondamage", "show or hide a chat line for each hit your pets land"),
                new ServerCommand("/summonattackontarget", "pets switch to the monster you last hit (on or off)"),
                new ServerCommand("/relaria", "Aun Relaria repeat-kill aura visual: /relaria aura on|off"),
                new ServerCommand("/charsheet", "publish a web character sheet (off, rotate)"),
                new ServerCommand("/fi", "resend nearby monsters and NPCs (invisible creatures), once a minute"),
            },
            Discord = "https://discord.gg/p2h3yca4BD",
            Wiki = "https://wiki.acdreamweave.com",
            Source = "public AC server list (acresources/serverslist), checked 2026-10-13; features and commands from " +
                     "github.com/Awful-Waffle-Rofl/ACE-DreamWeave (4aa7de6), checked 2026-10-14",
        };
}
