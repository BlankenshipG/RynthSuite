// IltBankCommands.cs — The /bank commands the Banking tab sends, and the reply lines it collects.
//
// Server contract (ACECustom PlayerCommands.HandleBank / Player_Bank, upstream 2026-10-11):
//   /b d [code]          deposit everything (no code) or one kind; "Deposited N <kind>",
//                        "No <kind> found to deposit", "Deposited all <kind>!".
//   /b lum               bank luminance tokens: "Starting Banked Lum", one "Deposited N x <token>"
//                        a kind, "Ending Banked Lum"; "No luminance tokens found to deposit".
//   /b w <code> <amount> withdraw: "Withdrew ...", "You don't have enough ...", "Failed to create ...".
//   /b w n <denom> [n]   trade notes by denomination (i v x l c d m md mm mmd).
//   /b t <code> <n> <who> transfer (IltBanking); Prestige Coins can't be transferred.
// Deposit, withdraw and transfer share one server throttle (bank_command_limit); a plain
// /bank balance doesn't touch it. Pure: no host calls, so the host tests cover it.
using System;

namespace RynthCore.Plugin.RynthAi.IltHub;

internal static class IltBankCommands
{
    /// <summary>A deposit choice: label, and the command's argument after "/b d" ("" = all).</summary>
    public static readonly (string Label, string Args)[] Deposits =
    {
        ("Everything", ""),
        ("Pyreals", "p"),
        ("Pyreal peas", "ps"),
        ("Luminance", "l"),
        ("Luminance tokens", "lum"),
        ("Legendary keys", "k"),
        ("Mythical keys", "mk"),
        ("Enlightened coins", "e"),
        ("Weakly enlightened coins", "we"),
        ("Prestige coins", "pc"),
        ("Trade notes", "n"),
    };

    /// <summary>A withdraw choice: label and the server code; "n" is trade notes (by denomination).</summary>
    public static readonly (string Label, string Code)[] Withdrawals =
    {
        ("Pyreals", "p"),
        ("Luminance", "l"),
        ("Legendary keys", "k"),
        ("Mythical keys", "mk"),
        ("Enlightened coins", "e"),
        ("Weakly enlightened coins", "we"),
        ("Prestige coins", "pc"),
        ("Trade notes", "n"),
    };

    /// <summary>Trade note denominations: label and code, in the server's order.</summary>
    public static readonly (string Label, string Code, long Value)[] NoteDenominations =
    {
        ("I (100)", "i", 100), ("V (500)", "v", 500), ("X (1K)", "x", 1_000), ("L (5K)", "l", 5_000),
        ("C (10K)", "c", 10_000), ("D (50K)", "d", 50_000), ("M (100K)", "m", 100_000),
        ("MD (150K)", "md", 150_000), ("MM (200K)", "mm", 200_000), ("MMD (250K)", "mmd", 250_000),
    };

    /// <summary>The server refuses a trade-note withdrawal of more than this many notes.</summary>
    public const int MaxNoteCount = 50_000;

    /// <summary>"/b d", "/b d p", or "/b lum" for the luminance tokens; empty for an unknown index.</summary>
    public static string Deposit(int index)
    {
        if (index < 0 || index >= Deposits.Length) return string.Empty;
        string args = Deposits[index].Args;
        if (args.Length == 0) return "/b d";
        return args == "lum" ? "/b lum" : "/b d " + args;
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
        if (code != "n") return $"/b w {code} {amount}";
        if (denomination < 0 || denomination >= NoteDenominations.Length) { error = "Pick a note denomination."; return string.Empty; }
        if (amount > MaxNoteCount) { error = $"At most {MaxNoteCount:N0} notes at a time."; return string.Empty; }
        return $"/b w n {NoteDenominations[denomination].Code} {amount}";
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
}
