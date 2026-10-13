using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

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
        r.Add("bank: one item table matches ACECustom's handler (deposit / withdraw / transfer)", ItemTable);
        r.Add("bank: transfer commands, trade notes by denomination, quoted names", Transfers);
        r.Add("bank: transfer replies (notes' equivalent, bound prestige coins) are recognised", TransferReplies);
    }

    private static int TransferIndex(string code) =>
        System.Array.FindIndex(IltBankCommands.Transfers, t => t.Code == code);

    private static void ItemTable()
    {
        string Codes(System.Collections.Generic.IEnumerable<string> codes) => string.Join(",", codes);
        // ACECustom HandleBank (upstream 976606695): deposit everything; withdraw all but peas and
        // luminance tokens; transfer all but those and the bound prestige coins.
        Check.Eq(Codes(System.Linq.Enumerable.Select(IltBankCommands.Deposits, d => d.Args)), ",p,ps,l,lum,k,mk,e,we,pc,n", "deposits");
        Check.Eq(Codes(System.Linq.Enumerable.Select(IltBankCommands.Withdrawals, w => w.Code)), "p,l,k,mk,e,we,pc,n", "withdrawals");
        Check.Eq(Codes(System.Linq.Enumerable.Select(IltBankCommands.Transfers, t => t.Code)), "p,l,k,mk,e,we,n", "transfers");
        Check.Eq(TransferIndex("pc"), -1, "prestige coins are bound: never offered for transfer");
        Check.Eq(TransferIndex("ps"), -1, "pyreal peas: deposit only");
        Check.Eq(IltBanking.Currencies.Length, IltBankCommands.Transfers.Length, "the transfer form uses the table");
        Check.Eq(IltBanking.Currencies[0].Code, "p", "saved transfer index 0 is still pyreals");
        Check.Eq(IltBanking.Currencies[5].Code, "we", "saved transfer indexes 0-5 keep their currency");
    }

    private static void Transfers()
    {
        int mmd = System.Array.FindIndex(IltBankCommands.NoteDenominations, n => n.Code == "mmd");
        Check.Eq(IltBankCommands.Transfer(TransferIndex("p"), 250_000, -1, "Bob", out _), "/bank t p 250000 Bob", "pyreals");
        Check.Eq(IltBankCommands.Transfer(TransferIndex("mk"), 3, -1, " Bob ", out _), "/bank t mk 3 Bob", "name trimmed");
        Check.Eq(IltBankCommands.Transfer(TransferIndex("l"), 10, -1, "Some Name", out _), "/bank t l 10 \"Some Name\"",
                 "a name with a space is quoted (the server reads one argument)");
        Check.Eq(IltBankCommands.Transfer(TransferIndex("n"), 4, mmd, "Bob", out _), "/bank t n mmd 4 Bob", "four 250K notes");
        Check.True(IltBankCommands.IsNoteTransfer(TransferIndex("n")), "notes transfer by denomination");
        Check.False(IltBankCommands.IsNoteTransfer(TransferIndex("p")), "pyreals don't");
        Check.Eq(IltBankCommands.NoteValue(mmd, 4), 1_000_000L, "4 x 250K");

        Check.Eq(IltBankCommands.Transfer(-1, 10, -1, "Bob", out string e1), "", "no currency refused");
        Check.Eq(e1, "Pick a currency.", "why");
        Check.Eq(IltBankCommands.Transfer(TransferIndex("p"), 0, -1, "Bob", out _), "", "zero refused");
        Check.Eq(IltBankCommands.Transfer(TransferIndex("p"), 10, -1, "  ", out string e2), "", "no recipient refused");
        Check.Eq(e2, "Enter a recipient.", "why");
        Check.Eq(IltBankCommands.Transfer(TransferIndex("p"), 10, -1, "Bob\" x", out _), "", "a quote can't break out of the name");
        Check.Eq(IltBankCommands.Transfer(TransferIndex("n"), 4, 99, "Bob", out _), "", "bad denomination refused");
        Check.Eq(IltBankCommands.Transfer(TransferIndex("n"), IltBankCommands.MaxNoteCount + 1, mmd, "Bob", out _), "", "too many notes refused");
    }

    private static void TransferReplies()
    {
        Check.True(IltBankCommands.IsTransferReply("Transferred 1,000,000 Pyreals to Bob"), "result");
        Check.True(IltBankCommands.IsTransferReply("(Equivalent to 4x MMD notes)"), "trade-note equivalent");
        Check.True(IltBankCommands.IsTransferReply("[BANK] Prestige Coins are bound to you and cannot be transferred."), "bound coins");
        Check.True(IltBankCommands.IsTransferReply("[Transfer] This command may only be run once every 5 seconds."), "throttled");
        Check.True(IltBankCommands.IsTransferReply("Invalid denomination. Use: i(100), v(500)"), "bad denomination");
        Check.False(IltBankCommands.IsTransferReply("[BANK] Pyreals: 1,000"), "a balance line is not");
        Check.False(IltBankCommands.IsTransferReply("Bob tells you, \"Transferred 5\""), "a tell is not");
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
