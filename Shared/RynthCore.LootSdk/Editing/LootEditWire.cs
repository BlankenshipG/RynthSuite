using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RynthCore.Loot.Editing;

// The JSON contract between RynthAi's loot edit bridge and the engine's ImGui
// Loot Editor face (RynthCore docs/IMGUI_LOOT_EDITOR.md). The engine mirrors
// these types in UI\Data\LootEditData.cs; keep the two in step. Strings cross
// the export boundary as UTF-8.

/// <summary>The open profile: header, the rule list (one row per rule) and the last command's result.</summary>
public sealed class LootEditState
{
    /// <summary>Changes whenever anything below changes.</summary>
    public long Revision { get; set; }

    /// <summary>Full path of the open profile; empty when none is open.</summary>
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    /// <summary>"utl", "json", or empty.</summary>
    public string Format { get; set; } = string.Empty;
    /// <summary>False for a new profile that Save will create.</summary>
    public bool Exists { get; set; }
    public bool ReadOnly { get; set; }
    public string ReadOnlyReason { get; set; } = string.Empty;
    public bool Dirty { get; set; }
    /// <summary>The file changed on disk while there were unsaved edits: Save refuses unless forced.</summary>
    public bool ChangedOnDisk { get; set; }

    /// <summary>RynthAi's loot profile (what looting uses now).</summary>
    public string InUsePath { get; set; } = string.Empty;
    public bool InUse { get; set; }

    /// <summary>Result of the last command (or load); MessageSeq counts them.</summary>
    public string Message { get; set; } = string.Empty;
    public bool MessageOk { get; set; } = true;
    public long MessageSeq { get; set; }
    /// <summary>The rule the last command made or moved (to select it), or -1.</summary>
    public int Focus { get; set; } = -1;

    /// <summary>Profiles in the loot folder, for the picker.</summary>
    public List<LootEditFile> Files { get; set; } = new();
    public List<LootEditRow> Rules { get; set; } = new();

    /// <summary>
    /// The "Add to loot profile" popup's rule (item_preview / item_add), or null.
    /// Added 2026-10; an older engine ignores it.
    /// </summary>
    public LootEditItemDraft? ItemDraft { get; set; }
}

public sealed class LootEditFile
{
    public string Path { get; set; } = string.Empty;
    public string Display { get; set; } = string.Empty;
}

/// <summary>One rule in the list.</summary>
public sealed class LootEditRow
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    /// <summary>VTank action code: 1 Keep, 2 Salvage, 3 Sell, 4 Read, 10 Keep #.</summary>
    public int Action { get; set; }
    public int KeepCount { get; set; }
    /// <summary>Conditions in words (ASCII), shortened.</summary>
    public string Summary { get; set; } = string.Empty;
    public int Conditions { get; set; }
}

/// <summary>A whole rule: what the rule view edits and sends back with "update_rule".</summary>
public sealed class LootEditRule
{
    public int Index { get; set; } = -1;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Action { get; set; } = 1;
    public int KeepCount { get; set; }
    /// <summary>Passed through untouched (Phase 1 does not edit these).</summary>
    public int Priority { get; set; }
    /// <summary>Null for v0 files, which have no custom expression line.</summary>
    public string? CustomExpression { get; set; }
    /// <summary>Every condition node in file order, DisabledRule (9999) nodes included.</summary>
    public List<LootEditCondition> Conditions { get; set; } = new();
}

public sealed class LootEditCondition
{
    /// <summary>VTank node type; -1 for a read-only JSON condition shown as text.</summary>
    public int NodeType { get; set; }
    /// <summary>The v1 length-code line, kept verbatim ("0" for new conditions).</summary>
    public string LengthCode { get; set; } = "0";
    /// <summary>The data lines, raw.</summary>
    public List<string> Lines { get; set; } = new();
}

/// <summary>An edit from the face. Commands that target a rule carry the name the face saw (Expect).</summary>
public sealed class LootEditCommand
{
    /// <summary>open, reload, save, set_enabled, move, add, duplicate, delete, rename, update_rule.</summary>
    public string Op { get; set; } = string.Empty;
    public int Index { get; set; } = -1;
    /// <summary>move: the index the rule ends up at.</summary>
    public int To { get; set; } = -1;
    public string Value { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    /// <summary>The rule's name as the face saw it; a mismatch means the list changed under it.</summary>
    public string? Expect { get; set; }
    /// <summary>open/reload: discard unsaved edits; save: overwrite a file that changed on disk.</summary>
    public bool Force { get; set; }
    public LootEditRule? Rule { get; set; }
    /// <summary>item_preview / item_add: the item and the popup's choices (added 2026-10).</summary>
    public LootEditItemRequest? Item { get; set; }
}

/// <summary>
/// "Add to loot profile" from a clicked item (added 2026-10): what the popup
/// asks for. item_preview builds the rule into LootEditState.ItemDraft;
/// item_add builds it and adds it to the profile (saved, RynthAi reloads it);
/// item_close drops the draft.
/// </summary>
public sealed class LootEditItemRequest
{
    /// <summary>The item; 0 = the item selected in the game.</summary>
    public uint ItemId { get; set; }
    /// <summary>LootItemMatch: 0 name + class, 1 name only, 2 items like this.</summary>
    public int Match { get; set; }
    /// <summary>VTank action code (1 Keep, 2 Salvage, 3 Sell, 4 Read, 10 Keep #); 0 = the item's default.</summary>
    public int Action { get; set; }
    /// <summary>Keep # count; -1 = default (one full stack).</summary>
    public int KeepCount { get; set; } = -1;
    /// <summary>Empty = the default name ("Keep Copper Pea").</summary>
    public string RuleName { get; set; } = string.Empty;
    /// <summary>True: the profile open in the Loot Editor; false: the one RynthAi loots with.</summary>
    public bool ToOpenProfile { get; set; }
    /// <summary>Echoed in the draft so the popup knows the answer is to its latest ask.</summary>
    public int Seq { get; set; }
}

/// <summary>The rule an item makes, as the popup shows it, and what happened when it was added.</summary>
public sealed class LootEditItemDraft
{
    public int Seq { get; set; }
    /// <summary>False: Error says why there is no rule (no item selected, read-only profile...).</summary>
    public bool Ok { get; set; }
    public string Error { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public bool Stackable { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public string TargetFile { get; set; } = string.Empty;
    /// <summary>The target is the profile RynthAi loots with.</summary>
    public bool TargetInUse { get; set; }
    /// <summary>"utl" or "json".</summary>
    public string Format { get; set; } = string.Empty;
    /// <summary>What was built (the defaults filled in).</summary>
    public int Match { get; set; }
    public int Action { get; set; }
    public int KeepCount { get; set; }
    public string RuleName { get; set; } = string.Empty;
    public string DefaultRuleName { get; set; } = string.Empty;
    /// <summary>The rule in words: name, action, one line per condition (ASCII).</summary>
    public List<string> Preview { get; set; } = new();
    /// <summary>Where it goes (0-based) among RuleCount rules, and why there.</summary>
    public int InsertAt { get; set; } = -1;
    public int RuleCount { get; set; }
    public string OrderNote { get; set; } = string.Empty;
    public List<string> Notes { get; set; } = new();
    /// <summary>item_add went through; Message says what was added where.</summary>
    public bool Added { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>Names for the face's pickers. Sent once per plugin load.</summary>
public sealed class LootEditVocab
{
    public List<LootEditName> Actions { get; set; } = new();
    public List<LootEditNodeType> NodeTypes { get; set; } = new();
    public List<LootEditName> ObjectClasses { get; set; } = new();
    public List<LootEditName> LongKeys { get; set; } = new();
    public List<LootEditName> DoubleKeys { get; set; } = new();
    public List<LootEditName> StringKeys { get; set; } = new();
    public List<LootEditName> Skills { get; set; } = new();
    /// <summary>
    /// Names for long keys' values (WieldSkilltype's skills, MaterialType's
    /// materials...), added 2026-10. An older engine ignores this; a newer engine
    /// with an older RynthAi gets it empty and keeps the value a text box.
    /// </summary>
    public List<LootEditValueTable> LongValueTables { get; set; } = new();
}

public sealed class LootEditName
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>What one long key's numbers mean (LootRuleText.ValueTableFor).</summary>
public sealed class LootEditValueTable
{
    /// <summary>The long key (e.g. 159 WieldSkilltype).</summary>
    public int Key { get; set; }
    /// <summary>The value is a set of flags: a named value is one flag (or a common pair).</summary>
    public bool Flags { get; set; }
    public List<LootEditName> Values { get; set; } = new();
}

public sealed class LootEditNodeType
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Data lines the node takes.</summary>
    public int Lines { get; set; }
    public List<string> Labels { get; set; } = new();
    public List<string> Defaults { get; set; } = new();
    /// <summary>
    /// The face's editor: "class" (object class), "keyval" (value, key), "keypattern"
    /// (pattern, key), "text" (one string), "value" (one number), "raw" (the lines as text).
    /// </summary>
    public string Editor { get; set; } = "raw";
    /// <summary>For keyval/keypattern: "long", "double", "string" or "skill".</summary>
    public string KeyTable { get; set; } = string.Empty;
    /// <summary>
    /// keyval "long" only: the value is picked by name when the key has a
    /// LongValueTables entry (GE/LE/E/NE and buffed GE; not "has flag"). Added 2026-10.
    /// </summary>
    public bool NamedValues { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LootEditState))]
[JsonSerializable(typeof(LootEditRule))]
[JsonSerializable(typeof(LootEditCommand))]
[JsonSerializable(typeof(LootEditVocab))]
public sealed partial class LootEditJsonContext : JsonSerializerContext { }
