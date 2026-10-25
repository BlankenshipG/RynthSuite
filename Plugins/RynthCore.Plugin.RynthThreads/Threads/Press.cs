using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthThreads.Threads;

/// <summary>One number in a modifier's description, with the form it was written in.</summary>
internal readonly struct ModNumber
{
    /// <summary>"x1.38" multiplier.</summary>
    public const string Mult = "mult";
    /// <summary>"12%".</summary>
    public const string Pct = "pct";
    /// <summary>"+29" (or "-5"): a signed amount, e.g. a rating.</summary>
    public const string Plus = "plus";
    /// <summary>A plain number.</summary>
    public const string Num = "num";

    public readonly double Value;
    public readonly string Kind;
    /// <summary>As written ("x1.38", "12%", "+29", "75").</summary>
    public readonly string Text;

    public ModNumber(double value, string kind, string text)
    {
        Value = value;
        Kind = kind;
        Text = text;
    }
}

/// <summary>One "Name - description" line of the gem's block.</summary>
internal sealed class GemModifier
{
    public string Name = string.Empty;
    public string Description = string.Empty;
    public List<ModNumber> Numbers = new();
}

/// <summary>
/// One press at a Fragment Press, as the server told it in chat. Everything the server says is
/// kept as said (numbers raw): "Level 190, 4 entries." goes into ResultLevel and Entries without
/// guessing what entries are.
/// </summary>
internal sealed class Press
{
    /// <summary>Random id, so a press read back from the shared file isn't counted twice.</summary>
    public string Id = string.Empty;
    public DateTime TimeUtc;
    public string Server = string.Empty;
    public string Character = string.Empty;

    /// <summary>"Raw Fragment" from "Using the Raw Fragment (Level 185) with the Fragment Press".</summary>
    public string Fragment = string.Empty;
    public int? FragmentLevel;
    /// <summary>"Fragment Press".</summary>
    public string Tool = string.Empty;

    /// <summary>The "Pressed. ..." line ("Pressed. It holds."), or "" when none came.</summary>
    public string Outcome = string.Empty;
    /// <summary>A "Components selected: ..." (or "No components ...") line came.</summary>
    public bool ComponentsSeen;
    /// <summary>Sorted, case-insensitively.</summary>
    public List<string> Components = new();
    /// <summary>The "... drawn at random." line, or "".</summary>
    public string RandomNote = string.Empty;
    public List<GemModifier> Modifiers = new();
    public int? ResultLevel;
    public int? Entries;

    /// <summary>Ended on its "Level N, M entries." line. False: timed out, or another "Using ..." line came first.</summary>
    public bool Complete;
    /// <summary>Why it ended: "done", "timeout", "next use", "logout".</summary>
    public string End = string.Empty;

    /// <summary>The press's own lines, in order.</summary>
    public List<string> Raw = new();
    /// <summary>Lines that came while the press was open but didn't fit it (other players' chat, or text not known yet).</summary>
    public List<string> Extra = new();

    /// <summary>The press says "It holds." (the only success text seen so far).</summary>
    public bool Held => Outcome.IndexOf("holds", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Used by the inference: a finished press with its whole gem block.</summary>
    public bool Usable => Complete && Modifiers.Count > 0;

    public bool HasComponent(string name)
    {
        foreach (string c in Components)
            if (string.Equals(c, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public bool HasModifier(string name)
    {
        foreach (GemModifier m in Modifiers)
            if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public GemModifier? FindModifier(string name)
    {
        foreach (GemModifier m in Modifiers)
            if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

    public string ComponentsText => Components.Count == 0 ? "no components" : string.Join(", ", Components);

    public static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 16);
}
