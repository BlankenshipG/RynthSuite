using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using RynthCore.Loot.VTank;

namespace RynthCore.Loot.Editing;

// "Find items like this" (2026-10-24): the Inventory panel's right-click builds a rule with the
// "Add to loot profile" checklist, but instead of saving it, filters with it. For the current
// inventory RynthAi judges each item itself (its looter's evaluator); for Global Inventory the
// rule travels to RynthInventory as one chat-command argument, "/ginv like <spec>", and is
// judged there on the saved items (LootFactsMatcher). This is that argument.
//
// Format: base64url (no padding) of UTF-8 text, one line each:
//   rf1                                 the tag (version)
//   <label>                             what the filter is called ("like Gold Ring")
//   <nodeType>\t<line>\t<line>...       one per condition, its VTank data lines
// Tabs and line breaks inside a line are turned into spaces (no condition the checklist makes
// has either). Unknown tags are refused, so a newer engine's spec can't be misread.

/// <summary>A filter rule as it travels to Global Inventory: a label and the rule's conditions.</summary>
public sealed class LootFindSpec
{
    public const string Tag = "rf1";
    /// <summary>The longest encoded spec accepted (a checklist rule is a few hundred characters).</summary>
    public const int MaxLength = 16 * 1024;

    public string Label { get; set; } = string.Empty;
    public List<VTankLootCondition> Conditions { get; } = new();

    /// <summary>The spec for <paramref name="conditions"/> (disabled-rule markers left out).</summary>
    public static string Encode(string label, IEnumerable<VTankLootCondition> conditions)
    {
        var sb = new StringBuilder(256);
        sb.Append(Tag).Append('\n').Append(Clean(label)).Append('\n');
        foreach (VTankLootCondition c in conditions)
        {
            if (c.NodeType == VTankNodeTypes.DisabledRule) continue;
            sb.Append(c.NodeType.ToString(CultureInfo.InvariantCulture));
            foreach (string line in c.DataLines) sb.Append('\t').Append(Clean(line));
            sb.Append('\n');
        }
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Reads a spec; false (with a reason for the player) when it isn't one.</summary>
    public static bool TryDecode(string? text, out LootFindSpec spec, out string error)
    {
        spec = new LootFindSpec();
        error = string.Empty;
        string s = (text ?? string.Empty).Trim();
        if (s.Length == 0 || s.Length > MaxLength)
        {
            error = s.Length == 0 ? "No filter given." : "The filter is too long.";
            return false;
        }
        string body;
        try
        {
            string b64 = s.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            body = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch (FormatException)
        {
            error = "That isn't a filter from the Inventory panel.";
            return false;
        }
        string[] lines = body.Replace("\r", string.Empty).Split('\n');
        if (lines.Length < 2 || lines[0] != Tag)
        {
            error = "That filter comes from a newer or older RynthAi; right-click the item again.";
            return false;
        }
        spec.Label = lines[1].Trim();
        for (int i = 2; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            string[] parts = lines[i].Split('\t');
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type))
            {
                error = "The filter is damaged (a condition without a type).";
                return false;
            }
            var data = new List<string>(parts.Length - 1);
            for (int j = 1; j < parts.Length; j++) data.Add(parts[j]);
            spec.Conditions.Add(new VTankLootCondition(type, "0", data));
        }
        return true;
    }

    /// <summary>The conditions in words, one line each ("Class is Jewelry", "Workmanship >= 7").</summary>
    public List<string> Describe()
    {
        var lines = new List<string>(Conditions.Count);
        foreach (VTankLootCondition c in Conditions) lines.Add(LootRuleText.Condition(c));
        return lines;
    }

    private static string Clean(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
