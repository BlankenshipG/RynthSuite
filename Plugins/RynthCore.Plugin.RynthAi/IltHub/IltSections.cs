// IltSections.cs - The ILT Hub's sections, each its own window, opened from the Mini Remote's
// Options or "/ra hub open <section>". Open flags live in IltCharacterState (per character).
using System;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal enum IltSection
{
    Character,
    Quests,
    Pet,
    Banking,
    Gear,
    Games,
}

internal static class IltSections
{
    /// <summary>Menu order (the old Hub's tab order).</summary>
    public static readonly IltSection[] All =
    {
        IltSection.Character, IltSection.Quests, IltSection.Pet,
        IltSection.Banking, IltSection.Gear, IltSection.Games,
    };

    public static string Label(IltSection s) => s switch
    {
        IltSection.Character => "Character",
        IltSection.Quests => "Quests",
        IltSection.Pet => "Pets",
        IltSection.Banking => "Banking",
        IltSection.Gear => "Gear",
        IltSection.Games => "Games",
        _ => s.ToString(),
    };

    /// <summary>Parses a "/ra hub open" section name (a few aliases accepted).</summary>
    public static bool TryParse(string text, out IltSection section)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "character": case "char": case "rates": section = IltSection.Character; return true;
            case "quests": case "quest": section = IltSection.Quests; return true;
            case "pets": case "pet": section = IltSection.Pet; return true;
            case "banking": case "bank": section = IltSection.Banking; return true;
            case "gear": section = IltSection.Gear; return true;
            case "games": case "game": section = IltSection.Games; return true;
            default: section = IltSection.Character; return false;
        }
    }

    public static bool IsOpen(IltCharacterState cs, IltSection s) => s switch
    {
        IltSection.Character => cs.CharacterWindowOpen,
        IltSection.Quests => cs.QuestTrackerPoppedOut,
        IltSection.Pet => cs.PetsWindowOpen,
        IltSection.Banking => cs.BankingWindowOpen,
        IltSection.Gear => cs.GearWindowOpen,
        IltSection.Games => cs.GamesWindowOpen,
        _ => false,
    };

    public static void SetOpen(IltCharacterState cs, IltSection s, bool open)
    {
        switch (s)
        {
            case IltSection.Character: cs.CharacterWindowOpen = open; break;
            case IltSection.Quests: cs.QuestTrackerPoppedOut = open; break;
            case IltSection.Pet: cs.PetsWindowOpen = open; break;
            case IltSection.Banking: cs.BankingWindowOpen = open; break;
            case IltSection.Gear: cs.GearWindowOpen = open; break;
            case IltSection.Games: cs.GamesWindowOpen = open; break;
        }
    }

    /// <summary>True when any section window is open.</summary>
    public static bool AnyOpen(IltCharacterState cs)
    {
        foreach (IltSection s in All)
            if (IsOpen(cs, s)) return true;
        return false;
    }

    /// <summary>"show" / "hide" / "toggle" (anything else toggles) applied to the current state.</summary>
    public static bool Resolve(string mode, bool current) => (mode ?? string.Empty).ToLowerInvariant() switch
    {
        "show" or "on" or "open" => true,
        "hide" or "off" or "close" => false,
        _ => !current,
    };
}
