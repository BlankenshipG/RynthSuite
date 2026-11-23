using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>A spell from SpellData.txt. <see cref="Category"/> is ACE's SpellCategory (the stacking family), <see cref="Power"/> its difficulty.</summary>
internal readonly record struct SpellInfo(string Name, double Duration, bool IsDebuff, int Category = 0, int Power = 0);

/// <summary>
/// Spell names and base durations from RynthAi's embedded spell table (SpellData.txt, linked
/// into this plugin): tab separated, id, name, category, family, words, duration (s, -1 none),
/// difficulty, then three flags, the second of which marks a debuff. Loaded on first use. The
/// Gear tab falls back to it (category, power) when the client's portal.dat can't be read.
/// </summary>
internal static class SpellTable
{
    private static Dictionary<uint, SpellInfo>? _spells;

    public static bool TryGet(uint id, out SpellInfo info)
    {
        EnsureLoaded();
        return _spells!.TryGetValue(id, out info);
    }

    public static string Name(uint id) => TryGet(id, out SpellInfo s) ? s.Name : $"Spell {id}";

    public static int Count
    {
        get { EnsureLoaded(); return _spells!.Count; }
    }

    private static void EnsureLoaded()
    {
        if (_spells != null) return;
        var map = new Dictionary<uint, SpellInfo>();
        try
        {
            using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("RynthOracle.SpellData.txt");
            if (s != null)
            {
                using var reader = new StreamReader(s, detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is string line)
                {
                    string[] c = line.Split('\t');
                    if (c.Length < 2 || !uint.TryParse(c[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint id)) continue;
                    double duration = c.Length > 5 && double.TryParse(c[5].Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double d) ? d : -1;
                    bool debuff = c.Length > 8 && c[8].Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
                    int category = c.Length > 2 && int.TryParse(c[2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int cat) ? cat : 0;
                    int power = c.Length > 6 && int.TryParse(c[6].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pw) ? pw : 0;
                    map[id] = new SpellInfo(c[1].Trim(), duration, debuff, category, power);
                }
            }
        }
        catch
        {
            // A missing table only costs names ("Spell 1234").
        }
        _spells = map;
    }
}
