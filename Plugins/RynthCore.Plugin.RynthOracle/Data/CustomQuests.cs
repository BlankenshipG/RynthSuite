// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// A server's custom quest worklist (customquests.csv: Server, Name, QuestFlag, Url, Hint). Each
/// row carries its server tag, so only the current server's rows show (today: ConquestAC's).
/// A repeatable counts as done only while on cooldown (upstream's rule).
/// </summary>
internal sealed class CustomQuest
{
    public string Server = "", Name = "", Flag = "", Url = "", Hint = "";

    public bool IsComplete(QuestFlagStore flags, DateTime utcNow)
    {
        if (!flags.TryGet(Flag, out QuestFlag q)) return false;
        if (q.RepeatTime == TimeSpan.Zero) return q.Solves > 0;
        return !q.Ready(utcNow);
    }

    public string Status(QuestFlagStore flags, DateTime utcNow)
    {
        if (!flags.TryGet(Flag, out QuestFlag q)) return "ready";
        if (q.RepeatTime == TimeSpan.Zero) return q.Solves > 0 ? "completed" : "ready";
        return q.NextAvailable(utcNow);
    }
}

internal static class CustomQuestList
{
    public static List<CustomQuest> All { get; } = Load();

    public static IEnumerable<CustomQuest> For(ServerIdentity server)
    {
        foreach (CustomQuest q in All)
            if (server.Is(q.Server)) yield return q;
    }

    private static List<CustomQuest> Load()
    {
        var list = new List<CustomQuest>();
        using StreamReader r = Csv.OpenEmbedded("customquests.csv");
        r.ReadLine(); // Server,Name,QuestFlag,Url,Hint
        while (r.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] f = Csv.ParseLine(line);
            list.Add(new CustomQuest
            {
                Server = Csv.Field(f, 0), Name = Csv.Field(f, 1), Flag = Csv.Field(f, 2).ToLowerInvariant(),
                Url = Csv.Field(f, 3), Hint = Csv.Field(f, 4),
            });
        }
        return list;
    }
}
