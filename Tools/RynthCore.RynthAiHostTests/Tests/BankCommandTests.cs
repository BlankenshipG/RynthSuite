using RynthCore.Plugin.RynthAi.IltHub;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Banking tab (IltBankCommands): the deposit / withdraw commands match ACECustom's /bank handler
// (upstream 2026-10-11), and only the bank's own reply lines count as a deposit / withdraw result.
internal static class BankCommandTests
{
    public static void Register(Runner r)
    {
        r.Add("bank: deposit commands (everything, one kind, luminance tokens)", Deposits);
        r.Add("bank: withdraw commands, trade notes by denomination and count", Withdrawals);
        r.Add("bank: withdraw input is checked before anything is sent", WithdrawRefusals);
        r.Add("bank: reply lines are recognised, balance and chat lines are not", Replies);
    }

    private static int DepositIndex(string args) =>
        System.Array.FindIndex(IltBankCommands.Deposits, d => d.Args == args);

    private static int WithdrawIndex(string code) =>
        System.Array.FindIndex(IltBankCommands.Withdrawals, w => w.Code == code);

    private static void Deposits()
    {
        Check.Eq(IltBankCommands.Deposit(0), "/b d", "Everything is a plain deposit");
        Check.Eq(IltBankCommands.Deposit(DepositIndex("p")), "/b d p", "pyreals");
        Check.Eq(IltBankCommands.Deposit(DepositIndex("pc")), "/b d pc", "prestige coins");
        Check.Eq(IltBankCommands.Deposit(DepositIndex("lum")), "/b lum", "luminance tokens use /b lum");
        Check.Eq(IltBankCommands.Deposit(-1), "", "unknown index sends nothing");
        Check.Eq(IltBankCommands.Deposit(IltBankCommands.Deposits.Length), "", "past the end sends nothing");
    }

    private static void Withdrawals()
    {
        Check.Eq(IltBankCommands.Withdraw(WithdrawIndex("p"), 1_500_000, 0, out _), "/b w p 1500000", "pyreals");
        Check.Eq(IltBankCommands.Withdraw(WithdrawIndex("pc"), 3, 0, out _), "/b w pc 3", "prestige coins");
        int mmd = System.Array.FindIndex(IltBankCommands.NoteDenominations, n => n.Code == "mmd");
        Check.Eq(IltBankCommands.Withdraw(WithdrawIndex("n"), 5, mmd, out _), "/b w n mmd 5", "five 250K notes");
    }

    private static void WithdrawRefusals()
    {
        Check.Eq(IltBankCommands.Withdraw(WithdrawIndex("p"), 0, 0, out string e1), "", "zero refused");
        Check.True(e1.Length > 0, "zero says why");
        Check.Eq(IltBankCommands.Withdraw(-1, 10, 0, out _), "", "no currency refused");
        Check.Eq(IltBankCommands.Withdraw(WithdrawIndex("n"), 10, 99, out _), "", "bad denomination refused");
        Check.Eq(IltBankCommands.Withdraw(WithdrawIndex("n"), IltBankCommands.MaxNoteCount + 1, 0, out string e2), "",
            "more notes than the server allows refused");
        Check.True(e2.Contains("50,000"), "says the limit");
    }

    private static void Replies()
    {
        Check.True(IltBankCommands.IsActionReply("Deposited 12,345 pyreals"), "deposit");
        Check.True(IltBankCommands.IsActionReply("No pyreal peas found to deposit"), "nothing to deposit");
        Check.True(IltBankCommands.IsActionReply("Withdrew 2 prestige coins"), "withdraw");
        Check.True(IltBankCommands.IsActionReply("Starting Banked Lum: 5B (5,000,000,000)"), "token deposit start");
        Check.True(IltBankCommands.IsActionReply("[Withdraw] This command may only be run once every 5 seconds."), "throttled");
        Check.True(IltBankCommands.IsActionReply("You don't have enough luminance banked. Need 10 but only have 5."), "short");
        Check.False(IltBankCommands.IsActionReply("[BANK] Pyreals: 1,000"), "a balance line is not an action reply");
        Check.False(IltBankCommands.IsActionReply("No one is near."), "other 'No ...' lines are not");
        Check.False(IltBankCommands.IsActionReply("Bob tells you, \"Deposited 5 pyreals\""), "a tell is not");
        Check.False(IltBankCommands.IsActionReply(""), "empty");
    }
}
