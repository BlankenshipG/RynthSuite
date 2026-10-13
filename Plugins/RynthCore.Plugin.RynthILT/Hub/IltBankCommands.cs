// IltBankCommands.cs — The /bank commands the Banking tab sends, and the reply lines it collects.
// Originally SilentKelpie's ILT Hub (RynthAi IltHub/); moved to RynthILT 2026-10-13.
//
// Server contract (ACECustom PlayerCommands.HandleBank / Player_Bank, upstream 976606695):
//   /b d [code]          deposit everything (no code) or one kind; "Deposited N <kind>",
//                        "No <kind> found to deposit", "Deposited all <kind>!".
//   /b lum               bank luminance tokens: "Starting Banked Lum", one "Deposited N x <token>"
//                        a kind, "Ending Banked Lum"; "No luminance tokens found to deposit".
//   /b w <code> <amount> withdraw: "Withdrew ...", "You don't have enough ...", "Failed to create ...".
//   /b w n <denom> [n]   trade notes by denomination (i v x l c d m md mm mmd).
//   /b t <code> <n> <who>       transfer; "Transferred {N0} {Currency} to {Name}[ (offline)]".
//   /b t n <denom> <n> <who>    trade notes' pyreal value, then "(Equivalent to Nx DENOM notes)".
//   The recipient is one argument: a name with a space has to be in "quotes".
//   Prestige Coins are bound to the character: "[BANK] Prestige Coins are bound to you and
//   cannot be transferred." Pyreal peas and luminance tokens are deposit only.
// Items is the one table of what the server allows; the deposit, withdraw and transfer lists
// are built from it. Deposit, withdraw and transfer share one server throttle
// (bank_command_limit); a plain /bank balance doesn't touch it. Pure: no host calls, so the
// host tests cover it.
using System;
using System.Linq;

namespace RynthCore.Plugin.RynthILT.Hub;

internal static class IltBankCommands
{
    /// <summary>
    /// One bank item: label, server code, and what the server allows for it. For trade notes
    /// ("n") a withdrawal or transfer is by denomination and note count.
    /// </summary>
    internal sealed record BankItem(string Label, string Code, bool Deposit, bool Withdraw, bool Transfer);

    /// <summary>The trade-notes code: withdrawn and transferred by denomination.</summary>
    public const string NotesCode = "n";

    /// <summary>The luminance-tokens deposit: its own command, "/b lum".</summary>
    public const string LuminanceTokensCode = "lum";

    /// <summary>Everything the bank holds, in the server's order, with what it allows.</summary>
    public static readonly BankItem[] Items =
    {
        new("Pyreals", "p", Deposit: true, Withdraw: true, Transfer: true),
        new("Pyreal peas", "ps", Deposit: true, Withdraw: false, Transfer: false),
        new("Luminance", "l", Deposit: true, Withdraw: true, Transfer: true),
        new("Luminance tokens", LuminanceTokensCode, Deposit: true, Withdraw: false, Transfer: false),
        new("Legendary keys", "k", Deposit: true, Withdraw: true, Transfer: true),
        new("Mythical keys", "mk", Deposit: true, Withdraw: true, Transfer: true),
        new("Enlightened coins", "e", Deposit: true, Withdraw: true, Transfer: true),
        new("Weakly enlightened coins", "we", Deposit: true, Withdraw: true, Transfer: true),
        new("Prestige coins", "pc", Deposit: true, Withdraw: true, Transfer: false),
        new("Trade notes", NotesCode, Deposit: true, Withdraw: true, Transfer: true),
    };

    /// <summary>A deposit choice: label, and the command's argument after "/b d" ("" = all).</summary>
    public static readonly (string Label, string Args)[] Deposits =
        new[] { ("Everything", string.Empty) }
            .Concat(Items.Where(i => i.Deposit).Select(i => (i.Label, i.Code)))
            .ToArray();

    /// <summary>A withdraw choice: label and the server code; "n" is trade notes (by denomination).</summary>
    public static readonly (string Label, string Code)[] Withdrawals =
        Items.Where(i => i.Withdraw).Select(i => (i.Label, i.Code)).ToArray();

    /// <summary>A transfer choice: label and the server code; "n" moves trade notes' pyreal value.</summary>
    public static readonly (string Label, string Code)[] Transfers =
        Items.Where(i => i.Transfer).Select(i => (i.Label, i.Code)).ToArray();

    /// <summary>Trade note denominations: label and code, in the server's order.</summary>
    public static readonly (string Label, string Code, long Value)[] NoteDenominations =
    {
        ("I (100)", "i", 100), ("V (500)", "v", 500), ("X (1K)", "x", 1_000), ("L (5K)", "l", 5_000),
        ("C (10K)", "c", 10_000), ("D (50K)", "d", 50_000), ("M (100K)", "m", 100_000),
        ("MD (150K)", "md", 150_000), ("MM (200K)", "mm", 200_000), ("MMD (250K)", "mmd", 250_000),
    };

    /// <summary>The server refuses a trade-note withdrawal of more than this many notes; transfers use the same cap.</summary>
    public const int MaxNoteCount = 50_000;

    /// <summary>"/b d", "/b d p", or "/b lum" for the luminance tokens; empty for an unknown index.</summary>
    public static string Deposit(int index)
    {
        if (index < 0 || index >= Deposits.Length) return string.Empty;
        string args = Deposits[index].Args;
        if (args.Length == 0) return "/b d";
        return args == LuminanceTokensCode ? "/b lum" : "/b d " + args;
    }

    /// <summary>
    /// "/b w p 1000000", or "/b w n mmd 5" for trade notes (<paramref name="amount"/> is then the
    /// note count). Empty (with <paramref name="error"/>) when the input can't be sent.
    /// </summary>
    public static string Withdraw(int index, long amount, int denomination, out string error)
    {
        error = string.Empty;
        if (index < 0 || index >= Withdrawals.Length) { error = "Pick what to withdraw."; return string.Empty; }
        if (amount <= 0) { error = "Amount must be positive."; return string.Empty; }
        string code = Withdrawals[index].Code;
        if (code != NotesCode) return $"/b w {code} {amount}";
        if (!TryNote(denomination, amount, out string denom, out error)) return string.Empty;
        return $"/b w n {denom} {amount}";
    }

    /// <summary>
    /// "/bank t p 250000 Bob", "/bank t n mmd 4 \"Some Name\"" for trade notes (<paramref name="amount"/>
    /// is then the note count). <paramref name="target"/> must already be a checked character name
    /// (IltParse.CharacterName); a name with a space is quoted, as the server reads one argument.
    /// Empty (with <paramref name="error"/>) when the input can't be sent.
    /// </summary>
    public static string Transfer(int index, long amount, int denomination, string target, out string error)
    {
        error = string.Empty;
        if (index < 0 || index >= Transfers.Length) { error = "Pick a currency."; return string.Empty; }
        if (amount <= 0) { error = "Amount must be positive."; return string.Empty; }
        target = (target ?? string.Empty).Trim();
        if (target.Length == 0) { error = "Enter a recipient."; return string.Empty; }
        if (target.IndexOf('"') >= 0) { error = "Recipient must be a character name."; return string.Empty; }
        string who = target.IndexOf(' ') >= 0 ? "\"" + target + "\"" : target;
        string code = Transfers[index].Code;
        if (code != NotesCode) return $"/bank t {code} {amount} {who}";
        if (!TryNote(denomination, amount, out string denom, out error)) return string.Empty;
        return $"/bank t n {denom} {amount} {who}";
    }

    /// <summary>True when <paramref name="index"/> is the trade-notes transfer (denomination and count).</summary>
    public static bool IsNoteTransfer(int index) => index >= 0 && index < Transfers.Length && Transfers[index].Code == NotesCode;

    /// <summary>The pyreal value of <paramref name="count"/> notes of <paramref name="denomination"/> (0 if unknown).</summary>
    public static long NoteValue(int denomination, long count) =>
        denomination >= 0 && denomination < NoteDenominations.Length && count > 0 ? NoteDenominations[denomination].Value * count : 0;

    private static bool TryNote(int denomination, long count, out string code, out string error)
    {
        code = string.Empty;
        error = string.Empty;
        if (denomination < 0 || denomination >= NoteDenominations.Length) { error = "Pick a note denomination."; return false; }
        if (count > MaxNoteCount) { error = $"At most {MaxNoteCount:N0} notes at a time."; return false; }
        code = NoteDenominations[denomination].Code;
        return true;
    }

    /// <summary>
    /// True for a line the server sends in reply to a deposit or withdrawal (so the tab can show
    /// it as the result). Only lines the bank code writes; never "[BANK]" balance lines.
    /// </summary>
    public static bool IsActionReply(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        return line.StartsWith("Deposited ", StringComparison.Ordinal)
            || line.StartsWith("Withdrew ", StringComparison.Ordinal)
            || (line.StartsWith("No ", StringComparison.Ordinal) && line.EndsWith("to deposit", StringComparison.Ordinal))
            || line.StartsWith("Starting Banked Lum", StringComparison.Ordinal)
            || line.StartsWith("Ending Banked Lum", StringComparison.Ordinal)
            || line.StartsWith("Not deposited", StringComparison.Ordinal)
            || line.StartsWith("You don't have enough", StringComparison.Ordinal)
            || line.StartsWith("You cannot hold any more", StringComparison.Ordinal)
            || line.StartsWith("You have not been luminance flagged", StringComparison.Ordinal)
            || line.StartsWith("Failed to ", StringComparison.Ordinal)
            || line.StartsWith("Warning: Requested", StringComparison.Ordinal)
            || line.StartsWith("Amount must be", StringComparison.Ordinal)
            || line.StartsWith("Cannot deposit while", StringComparison.Ordinal)
            || line.StartsWith("[Deposit]", StringComparison.Ordinal)
            || line.StartsWith("[Withdraw]", StringComparison.Ordinal)
            || line.StartsWith("Invalid amount", StringComparison.Ordinal)
            || line.StartsWith("Invalid denomination", StringComparison.Ordinal)
            || line.StartsWith("Count must be", StringComparison.Ordinal)
            || line.StartsWith("Count cannot exceed", StringComparison.Ordinal)
            || line.StartsWith("You are not permitted to use bank commands", StringComparison.Ordinal);
    }

    /// <summary>
    /// True for a line the server sends in reply to a transfer: the result, the trade-note
    /// equivalent, the throttle, or a refusal (unknown player, bound prestige coins, bad input).
    /// </summary>
    public static bool IsTransferReply(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        return line.StartsWith("Transferred ", StringComparison.Ordinal)
            || line.StartsWith("(Equivalent to ", StringComparison.Ordinal)
            || line.StartsWith("Character '", StringComparison.Ordinal)
            || line.StartsWith("You cannot transfer", StringComparison.Ordinal)
            || line.StartsWith("You don't have enough", StringComparison.Ordinal)
            || line.StartsWith("[Transfer]", StringComparison.Ordinal)
            || line.StartsWith("[BANK] Too many parameters", StringComparison.Ordinal)
            || line.StartsWith("[BANK] Prestige Coins are bound", StringComparison.Ordinal)
            || line.StartsWith("Invalid count", StringComparison.Ordinal)
            || line.StartsWith("Invalid denomination", StringComparison.Ordinal);
    }
}
