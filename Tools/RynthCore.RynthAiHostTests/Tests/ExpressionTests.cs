using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The meta expression language (ExpressionEngine): the infix parser, coercions, recursion
// caps, and the expression functions. Character, world and time functions read the fake host.
// pvars/gvars go to the scratch folder (Program.cs redirects the paths before any test).
internal static class ExpressionTests
{
    private const uint Player = 0x50000A01;

    public static void Register(Runner r)
    {
        r.Add("expr: safety - var files point at the scratch folder", VarPathsRedirected);
        r.Add("expr: arithmetic and precedence", Arithmetic);
        r.Add("expr: unary minus, negative and hex literals", UnaryAndLiterals);
        r.Add("expr: comparisons are numeric", Comparisons);
        r.Add("expr: equality is numeric or case-insensitive text", Equality);
        r.Add("expr: logical and bitwise operators, & vs && and | vs ||", LogicalAndBitwise);
        r.Add("expr: regex operator #", RegexOperator);
        r.Add("expr: + concatenates text, other operators read text as 0", TextCoercion);
        r.Add("expr: backtick strings", Backticks);
        r.Add("expr: empty input, trailing text, stray characters", ParserEdges);
        r.Add("expr: truthiness (ToBool)", Truthiness);
        r.Add("expr: recursion and paren caps return an error instead of crashing", RecursionCaps);
        r.Add("expr: session variables", Variables);
        r.Add("expr: persistent and global variables (scratch folder)", PersistentVars);
        r.Add("expr: list functions", Lists);
        r.Add("expr: higher-order list functions restore $0 $1 $2", ListHigherOrder);
        r.Add("expr: dictionaries", Dicts);
        r.Add("expr: string, conversion and math functions", StringAndMath);
        r.Add("expr: control flow (iif, if, ifthen, exec) is lazy", ControlFlow);
        r.Add("expr: delayexec fires on the pump when due, in order", DelayExec);
        r.Add("expr: stopwatches", Stopwatches);
        r.Add("expr: object type, options and unknown functions", TypesOptionsUnknown);
        r.Add("expr: RynthAi settings and meta state from expressions", SettingsAndState);
        r.Add("expr: character properties, skills, vitals through the host", CharacterFunctions);
        r.Add("expr: position and coordinate functions", Coordinates);
        r.Add("expr: spell expiration", SpellExpiration);
        r.Add("expr: Dereth calendar with the clock unreadable (not in world)", GameTime);
        r.Add("expr: quest flags", Quests);
        r.Add("expr: fellowship functions with no tracker", FellowshipDefaults);
        r.Add("expr: world objects and inventory counts through the cache", WorldObjects);
        r.Add("expr: chat and echo", ChatFunctions);
        r.Add("expr: $name reads an unset variable like getvar[name]", DollarMatchesGetvar);   // was a known failure; fixed by 79a39b7
        r.Add("expr: arithmetic that gives negative zero is false", NegativeZeroIsFalse);   // was a known failure; fixed by 92a6318
        r.KnownFailure("expr: list items may contain commas or be lists",
            "lists are stored as \"[a,b]\" and split on every comma, so an item with a comma (or a nested list) becomes several items",
            ListItemsWithCommas);
        r.Add("expr: # takes a regex pattern without backticks, as documented", RegexOperatorUnquoted);   // was a known failure; fixed by c34b39c
        r.Add("expr: vitae[] reports no penalty when it cannot read vitae", VitaeWithoutHost);   // was a known failure; fixed by 33f23f7
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal static ExpressionEngine Engine(RynthCoreHost host = default, LegacyUiSettings? settings = null, uint playerId = 0)
    {
        var e = new ExpressionEngine(host);
        if (settings != null) e.SetSettings(settings);
        if (playerId != 0) e.SetPlayerId(playerId);
        return e;
    }

    private static void Is(ExpressionEngine e, string expr, string expected)
        => Check.Eq(e.Evaluate(expr), expected, $"[{expr}]");

    // ── Tests ─────────────────────────────────────────────────────────────────

    private static void VarPathsRedirected()
    {
        string root = Program.TempRoot;
        Check.True(ExpressionEngine.PvarsDir.StartsWith(root, StringComparison.OrdinalIgnoreCase), $"pvars dir {ExpressionEngine.PvarsDir}");
        Check.True(ExpressionEngine.GvarsPath.StartsWith(root, StringComparison.OrdinalIgnoreCase), $"gvars path {ExpressionEngine.GvarsPath}");
        Check.True(ExpressionEngine.ItemGiverDir.StartsWith(root, StringComparison.OrdinalIgnoreCase), $"item giver dir {ExpressionEngine.ItemGiverDir}");
    }

    private static void Arithmetic()
    {
        var e = Engine();
        Is(e, "1+2*3", "7");
        Is(e, "(1+2)*3", "9");
        Is(e, "10-2-3", "5");          // left to right
        Is(e, "100/10/5", "2");
        Is(e, "2*3%4", "2");           // * and % share a level, left to right
        Is(e, "7/2", "3.5");
        Is(e, "1/0", "0");             // division by zero is 0, not infinity
        Is(e, "5%0", "0");
        Is(e, "7%3", "1");
        Is(e, "-7%3", "-1");
        Is(e, "0.1+0.2", "0.30000000000000004");
        Is(e, "1e3+1", "1001");
        Is(e, "2 * ( 3 + 4 ) ", "14");
        Is(e, "((((1))))", "1");
        Is(e, "(1+2", "3");            // a missing ')' is tolerated
    }

    private static void UnaryAndLiterals()
    {
        var e = Engine();
        Is(e, "-3+5", "2");
        Is(e, "2*-3", "-6");
        Is(e, "5--3", "8");
        Is(e, "-(2+3)", "-5");
        Is(e, "- 2", "-2");
        Is(e, "--2", "2");
        Is(e, "0xFF", "255");
        Is(e, "0x10+1", "17");
        Is(e, "-0x10", "-16");
        Is(e, "0XfF", "255");
        Is(e, "0x", "0");
        Is(e, "~0", "-1");
        Is(e, "~5", "-6");
        Is(e, "1.5", "1.5");
    }

    private static void Comparisons()
    {
        var e = Engine();
        Is(e, "3>2", "1");
        Is(e, "2>3", "0");
        Is(e, "2>=2", "1");
        Is(e, "2<=1", "0");
        Is(e, "10>9", "1");            // numeric, not "10" < "9" as text
        Is(e, "abc<5", "1");           // text reads as 0
        Is(e, "3 < 4 == 1", "1");      // comparison binds tighter than ==
        Is(e, "1 < 2 < 3", "1");       // (1<2)=1, 1<3
        Is(e, "3 > 2 > 1", "0");       // (3>2)=1, 1>1 is false
    }

    private static void Equality()
    {
        var e = Engine();
        Is(e, "1==1.0", "1");
        Is(e, "0x10==16", "1");
        Is(e, "abc==ABC", "1");        // text compares ignoring case (M1)
        Is(e, "abc!=abd", "1");
        Is(e, "abc!=ABC", "0");
        Is(e, "`` == 0", "0");         // empty text is not the number 0
        Is(e, "1+1==2", "1");
        Is(e, "2==2&1", "1");          // & is below ==: (2==2)&1
    }

    private static void LogicalAndBitwise()
    {
        var e = Engine();
        Is(e, "1&&0", "0");
        Is(e, "0||2", "1");
        Is(e, "abc&&1", "1");
        Is(e, "false||0", "0");
        Is(e, "6|1", "7");
        Is(e, "7^3", "4");
        Is(e, "7&3", "3");
        Is(e, "1<<4", "16");
        Is(e, "16>>2", "4");
        Is(e, "3&1&&1", "1");          // & then &&
        Is(e, "1|2||0", "1");          // | then ||
        Is(e, "0&&1||1", "1");         // && binds tighter than ||
        Is(e, "1||0&&0", "1");
        Is(e, "5.9|0", "5");           // bitwise operators truncate
    }

    private static void RegexOperator()
    {
        var e = Engine();
        Is(e, "`Olthoi Soldier`#`^olthoi`", "1");       // case-insensitive
        Is(e, "`Olthoi Soldier`#`soldier$`", "1");
        Is(e, "abc#x", "0");
        Is(e, "abc#b", "1");                            // a plain word works without backticks
        Is(e, "abc#`(`", "0");                          // a bad pattern is no match, not an error
        Is(e, "abc#``", "0");                           // an empty pattern never matches
        Is(e, "abc#b == 1", "1");                       // # binds tighter than ==
    }

    private static void TextCoercion()
    {
        var e = Engine();
        Is(e, "hello+world", "helloworld");
        Is(e, "1+abc", "1abc");
        Is(e, "abc-1", "-1");
        Is(e, "abc*2", "0");
        Is(e, "`2`+`3`", "5");         // numeric text adds as numbers
    }

    private static void Backticks()
    {
        var e = Engine();
        Is(e, "`hello world`", "hello world");
        Is(e, "`a\\`b`", "a`b");
        Is(e, "`1+2`", "1+2");         // not evaluated
        Is(e, "`unterminated", "unterminated");
        Is(e, "``", "");
    }

    private static void ParserEdges()
    {
        var e = Engine();
        Is(e, "", "");
        Is(e, "   ", "");
        Is(e, "1 2", "1");             // text after a complete expression is ignored
        Is(e, "hello world", "hello"); // so a bare two-word value keeps only the first word
        Is(e, "@", "");
        Is(e, "!1", "");               // there is no logical not; use ==0 or isfalse[]
        Is(e, "a.b_c9", "a.b_c9");     // identifiers keep dots and underscores
        Check.Eq(e.Evaluate(null!), "", "null input");
    }

    private static void Truthiness()
    {
        foreach (string f in new[] { "", "0", "false", "FALSE", "ERR:x:y" })
            Check.False(ExpressionEngine.ToBool(f), $"'{f}' is false");
        foreach (string t in new[] { "1", "-1", "abc", "0.0", "00", " 0", "true" })
            Check.True(ExpressionEngine.ToBool(t), $"'{t}' is true");
    }

    private static void RecursionCaps()
    {
        var e = Engine();
        e.Evaluate("setvar[x, `exec[$x]`]");
        string r = e.Evaluate("exec[$x]");
        Check.True(r.StartsWith("ERR:Depth", StringComparison.Ordinal), $"self-recursive exec: {r}");

        string deepParens = new string('(', 300) + "1" + new string(')', 300);
        string p = e.Evaluate(deepParens);
        Check.True(p.StartsWith("ERR:Depth", StringComparison.Ordinal), $"300 nested parens: {p}");
        Is(e, new string('(', 200) + "1" + new string(')', 200), "1");   // under the cap

        // Deep function nesting hits the eval cap; the inner error then reads as 0.
        string deepCalls = "1";
        for (int i = 0; i < 100; i++) deepCalls = $"abs[{deepCalls}]";
        Check.Eq(e.Evaluate(deepCalls), "0", "100 nested calls: capped, no crash");

        // The engine is usable again after hitting a cap.
        Is(e, "1+1", "2");
        Is(e, "(((2)))", "2");
    }

    private static void Variables()
    {
        var e = Engine();
        Is(e, "getvar[n]", "0");
        Is(e, "testvar[n]", "0");
        Is(e, "setvar[n, 1+2]", "3");
        Is(e, "getvar[N]", "3");                 // names ignore case
        Is(e, "$n*2", "6");
        Is(e, "testvar[n]", "1");
        Is(e, "touchvar[n]", "1");
        Is(e, "touchvar[fresh]", "0");
        Is(e, "getvar[fresh]", "0");
        Is(e, "clearvar[n]", "1");
        Is(e, "clearvar[n]", "0");
        Is(e, "setvar[,5]", "5");                // empty name: returned, not stored
        Check.False(e.Variables.ContainsKey(""), "empty name not stored");
        Is(e, "setvar[a,1] && setvar[b,2] && clearallvars[]", "1");
        Is(e, "testvar[a]+testvar[b]", "0");
        Is(e, "setvar[s, `two words`]", "two words");
        Is(e, "$s", "two words");
        Is(e, "setvar[t, two words]", "two");    // unquoted: only the first word
        e.SetVariable("host_set", "7");
        Is(e, "$host_set", "7");
    }

    private static void PersistentVars()
    {
        string pvarDir = ExpressionEngine.PvarsDir;
        FakeHost.Reset();
        FakeHost.Names[Player] = "Test Toon";
        var host = FakeHost.Create();

        var e = Engine(host, playerId: Player);
        Is(e, "getpvar[k]", "0");
        Is(e, "setpvar[k, 42]", "42");
        Is(e, "touchpvar[k]", "1");
        Is(e, "touchpvar[other]", "0");
        Is(e, "clearpvar[other]", "1");
        Is(e, "clearpvar[other]", "0");
        Is(e, "setgvar[g, hello]", "hello");
        Is(e, "getgvar[G]", "hello");
        e.FlushVars(force: true);

        string pfile = Path.Combine(pvarDir, "Test Toon.txt");
        Check.True(File.Exists(pfile), "per-character file named after the character");
        Check.True(File.Exists(ExpressionEngine.GvarsPath), "global file written");
        if (File.Exists(pfile)) Check.Eq(File.ReadAllText(pfile).Trim(), "k\t42", "tab-separated file");

        var e2 = Engine(host, playerId: Player);
        Is(e2, "getpvar[k]", "42");
        Is(e2, "testpvar[k]", "1");
        Is(e2, "getgvar[g]", "hello");

        // Another character does not see the first one's pvars, but shares gvars.
        FakeHost.Names[Player + 1] = "Other Toon";
        var e3 = Engine(host, playerId: Player + 1);
        Is(e3, "getpvar[k]", "0");
        Is(e3, "getgvar[g]", "hello");

        // No player: pvars go to unknown.txt.
        var e4 = Engine(host);
        Is(e4, "setpvar[z,1]", "1");
        e4.FlushVars(force: true);
        Check.True(File.Exists(Path.Combine(pvarDir, "unknown.txt")), "no player id: unknown.txt");

        // Unforced flushes wait 2 s between writes; forced ones always write.
        long now = 1_000_000;
        var e5 = Engine(host, playerId: Player);
        e5.TickMs = () => now;
        Is(e5, "setgvar[g, one]", "one");
        e5.FlushVars();
        Check.Eq(Engine(host).Evaluate("getgvar[g]"), "one", "first flush writes");
        Is(e5, "setgvar[g, two]", "two");
        now += 1999;
        e5.FlushVars();
        Check.Eq(Engine(host).Evaluate("getgvar[g]"), "one", "throttled 1.999 s later");
        now += 1;
        e5.FlushVars();
        Check.Eq(Engine(host).Evaluate("getgvar[g]"), "two", "written 2 s later");

        // A malformed var file loads what it can.
        File.WriteAllLines(ExpressionEngine.GvarsPath, new[] { "no tab here", "\tvalue without key", "ok\tyes", "dup\t1", "dup\t2" });
        var e6 = Engine(host);
        Is(e6, "getgvar[ok]", "yes");
        Is(e6, "getgvar[dup]", "2");
        Is(e6, "testgvar[no tab here]", "0");
        Is(e6, "clearallgvars[]", "1");
        Is(e6, "testgvar[ok]", "0");
    }

    private static void Lists()
    {
        var e = Engine();
        Is(e, "listcreate[a,b,c]", "[a,b,c]");
        Is(e, "listcreate[]", "[]");
        Is(e, "listcreate[1+1, `x y`]", "[2,x y]");
        Is(e, "listcount[listcreate[a,b,c]]", "3");
        Is(e, "listcount[listcreate[]]", "0");
        Is(e, "listcount[notalist]", "0");
        e.Evaluate("setvar[l, listcreate[a,b,c,b]]");
        Is(e, "listgetitem[$l,0]", "a");
        Is(e, "listgetitem[$l,3]", "b");
        Is(e, "listgetitem[$l,4]", "");
        Is(e, "listgetitem[$l,-1]", "");
        Is(e, "listadd[$l,d]", "[a,b,c,b,d]");
        Is(e, "$l", "[a,b,c,b]");                 // lists are values: listadd returns a new one
        Is(e, "listinsert[$l,z,0]", "[z,a,b,c,b]");
        Is(e, "listinsert[$l,z,4]", "[a,b,c,b,z]");
        Is(e, "listinsert[$l,z,5]", "0");
        Is(e, "listremove[$l,b]", "[a,c,b]");     // first match only
        Is(e, "listremove[$l,q]", "[a,b,c,b]");
        Is(e, "listremoveat[$l,1]", "[a,c,b]");
        Is(e, "listremoveat[$l,9]", "0");
        Is(e, "listcontains[$l,C]", "1");         // ignores case
        Is(e, "listcontains[$l,q]", "0");
        Is(e, "listindexof[$l,b]", "1");
        Is(e, "listlastindexof[$l,b]", "3");
        Is(e, "listindexof[$l,q]", "-1");
        Is(e, "listcontains[listcreate[1,2],2.0]", "1");   // numbers compare as numbers
        Is(e, "listcopy[$l]", "[a,b,c,b]");
        Is(e, "listcopy[x]", "[]");
        Is(e, "listreverse[$l]", "[b,c,b,a]");
        Is(e, "listpop[$l]", "b");
        Is(e, "listpop[$l,0]", "a");
        Is(e, "listpop[$l,-5]", "b");             // any negative index means the last item
        Is(e, "listpop[listcreate[]]", "");
        Is(e, "listclear[$l]", "[]");
        Is(e, "listfromrange[1,3]", "[1,2,3]");
        Is(e, "listfromrange[3,1]", "[3,2,1]");
        Is(e, "listfromrange[2,2]", "[2]");
        Is(e, "listadd[notalist,x]", "0");
    }

    private static void ListHigherOrder()
    {
        var e = Engine();
        e.Evaluate("setvar[l, listcreate[3,1,2]]");
        Is(e, "listfilter[$l, `$1 > 1`]", "[3,2]");
        Is(e, "listfilter[$l, `$0 == 0`]", "[3]");          // $0 is the index
        Is(e, "listmap[$l, `$1 * 10`]", "[30,10,20]");
        Is(e, "listreduce[$l, `$2 + $1`]", "6");            // $2 is the running value
        Is(e, "listreduce[listcreate[], `$2 + $1`]", "0");
        Is(e, "listsort[listcreate[b,a,c]]", "[a,b,c]");
        Is(e, "listsort[listcreate[10,9,100]]", "[10,100,9]");            // default sort is text order
        Is(e, "listsort[listcreate[10,9,100], `$1 - $2`]", "[9,10,100]");  // comparator
        Is(e, "listfilter[$l, ``]", "[]");

        // The loop variables are put back afterwards.
        e.Evaluate("setvar[1, mine]");
        e.Evaluate("listmap[$l, `$1`]");
        Is(e, "getvar[1]", "mine");
        Is(e, "testvar[0]", "0");
        Is(e, "testvar[2]", "0");
        // A nested higher-order call puts the outer $1 back: 2 items > 5, times 10, plus the outer item.
        Is(e, "listmap[listcreate[1,2], `listcount[listfilter[listcreate[5,6,7], $1 > 5]] * 10 + $1`]", "[21,22]");
    }

    private static void Dicts()
    {
        var e = Engine();
        Is(e, "dictcreate[a,1,b,2]", "D:0");
        Is(e, "dictgetitem[dictcreate[a,1,b,2],b]", "2");
        Is(e, "setvar[d, dictcreate[a,1]] && dictadditem[$d,a,9]", "1");   // one evaluation: the handle is live
        // Dicts last for one top-level evaluation; a handle kept in a variable goes dead.
        Is(e, "dictgetitem[$d,a]", "");
        Is(e, "dictsize[$d]", "0");
        Is(e, "setvar[d, dictcreate[k,v]] && dictadditem[$d,n,1]==0 && dicthaskey[$d,n] && dictsize[$d]==2", "1");
        Is(e, "setvar[d, dictcreate[k,v]] && dictremovekey[$d,k] && dictremovekey[$d,k]==0 && dictsize[$d]==0", "1");
        Is(e, "listcount[dictkeys[dictcreate[a,1,b,2,c,3]]]", "3");
        Is(e, "dictvalues[dictcreate[a,1,b,2]]", "[1,2]");
        Is(e, "dicthaskey[dictcreate[Key,1],key]", "0");   // dict keys are case-sensitive
        Is(e, "setvar[d, dictcreate[a,1]] && setvar[c, dictcopy[$d]] && dictadditem[$c,z,1]==0 && dicthaskey[$d,z]==0", "1");
        Is(e, "dictsize[dictclear[dictcreate[a,1]]]", "0");
        Is(e, "dictcreate[a]", "D:0");                     // odd argument count: the last key is dropped
        Is(e, "dictsize[dictcreate[a]]", "0");
        Is(e, "dictgetitem[D:99,a]", "");
    }

    private static void StringAndMath()
    {
        var e = Engine();
        Is(e, "strlen[`hello world`]", "11");
        Is(e, "strlen[``]", "0");
        Is(e, "cstr[5]", "5");
        Is(e, "cstrf[3.14159, `F2`]", "3.14");
        Is(e, "cstrf[3.5, `Q`]", "3.5");        // a bad format falls back to G
        Is(e, "cnumber[abc]", "0");
        Is(e, "cnumber[`12.50`]", "12.5");
        Is(e, "ord[A]", "65");
        Is(e, "ord[``]", "0");
        Is(e, "chr[65]", "A");
        Is(e, "chr[-1]", "");
        Is(e, "chr[70000]", "");
        Is(e, "getregexmatch[`abc123`, `[0-9]+`]", "123");
        Is(e, "getregexmatch[abc123, `X`]", "");
        Is(e, "getregexmatch[abc, `(`]", "");
        // An unquoted pattern with brackets is evaluated as an expression first and comes out empty.
        Is(e, "getregexmatch[abc123,[0-9]+]", "");
        Is(e, "tostring[1+1]", "2");
        Is(e, "floor[-1.5]", "-2");
        Is(e, "ceiling[1.2]", "2");
        Is(e, "round[2.5]", "3");
        Is(e, "round[-2.5]", "-3");
        Is(e, "abs[-4]", "4");
        Is(e, "sqrt[16]", "4");
        Is(e, "sqrt[-1]", "NaN");
        Is(e, "hexstr[255]", "0xFF");
        Is(e, "hexstr[-1]", "0xFFFFFFFFFFFFFFFF");
        Is(e, "atan2[0,1]", "0");
        Is(e, "cos[0]", "1");
        Is(e, "isfalse[0]", "1");
        Is(e, "istrue[2]", "1");
        Is(e, "istrue[abc]", "0");               // text is not a true number here, though ToBool says true
        Is(e, "spellname[1332]", "Strength Self VI");
        Is(e, "spellname[99999999]", "Unknown Spell (99999999)");

        var seen = new HashSet<string>();
        for (int i = 0; i < 200; i++) seen.Add(e.Evaluate("randint[1,3]"));
        Check.True(seen.SetEquals(new[] { "1", "2" }), $"randint[1,3] gives 1 or 2 (upper bound excluded): {string.Join(",", seen)}");
        Is(e, "randint[5,5]", "5");
        Check.True(e.Evaluate("randint[5,1]").StartsWith("ERR:ArgumentOutOfRangeException", StringComparison.Ordinal), "randint with min > max is an error");
    }

    private static void ControlFlow()
    {
        var e = Engine();
        Is(e, "iif[1, setvar[a,1], setvar[b,1]]", "1");
        Is(e, "testvar[a]", "1");
        Is(e, "testvar[b]", "0");                 // the other branch never ran
        Is(e, "iif[0, x]", "");
        Is(e, "iif[1]", "");
        Is(e, "iif[abc, yes, no]", "yes");        // lazy iif uses ToBool: text is true
        Is(e, "iif[0.0, yes, no]", "yes");        // "0.0" is not "0"
        Is(e, "if[1, setvar[c,1]]", "1");
        Is(e, "if[0, setvar[d,1]]", "");
        Is(e, "testvar[d]", "0");
        Is(e, "ifthen[1, `1+1`, `2+2`]", "2");    // picks a branch text, then evaluates it
        Is(e, "ifthen[0, `1+1`, `2+2`]", "4");
        Is(e, "ifthen[0, `1+1`]", "");
        Is(e, "exec[`2*21`]", "42");
        Is(e, "exec[``]", "");
    }

    private static void DelayExec()
    {
        var e = Engine();
        long now = 5_000;
        e.TickMs = () => now;
        Is(e, "delayexec[1000, `setvar[x, first]`]", "1");
        Is(e, "delayexec[1000, `setvar[x, second]`]", "1");
        Is(e, "delayexec[-50, `setvar[neg, 1]`]", "1");
        Is(e, "delayexec[soon, `setvar[bad, 1]`]", "1");
        e.PumpDelayedExecs();
        Is(e, "getvar[neg]", "1");                // negative delay: next pump
        Is(e, "getvar[bad]", "1");                // unreadable delay: next pump
        Is(e, "testvar[x]", "0");
        now += 999;
        e.PumpDelayedExecs();
        Is(e, "testvar[x]", "0");
        now += 1;
        e.PumpDelayedExecs();
        Is(e, "getvar[x]", "second");             // both fired, in the order scheduled

        // A delayexec scheduled by a firing one waits for a later pump.
        // (\` is an escaped backtick inside a backtick string.)
        Is(e, "delayexec[0, `delayexec[0, \\`setvar[chain, 1]\\`]`]", "1");
        e.PumpDelayedExecs();
        Is(e, "testvar[chain]", "0");
        e.PumpDelayedExecs();
        Is(e, "getvar[chain]", "1");
    }

    private static void Stopwatches()
    {
        var e = Engine();
        Is(e, "setvar[sw, stopwatchcreate[]]", "SW:0");
        Is(e, "stopwatchcreate[]", "SW:1");
        Is(e, "stopwatchelapsedseconds[$sw]", "0");
        Is(e, "stopwatchstart[$sw]", "SW:0");
        System.Threading.Thread.Sleep(20);
        Is(e, "stopwatchstop[$sw]", "SW:0");
        double a = double.Parse(e.Evaluate("stopwatchelapsedseconds[$sw]"), System.Globalization.CultureInfo.InvariantCulture);
        System.Threading.Thread.Sleep(20);
        double b = double.Parse(e.Evaluate("stopwatchelapsedseconds[$sw]"), System.Globalization.CultureInfo.InvariantCulture);
        Check.True(a >= 0.015, $"ran while started: {a}");
        Check.Eq(a, b, "stopped: does not advance");
        Is(e, "stopwatchelapsedseconds[`SW:77`]", "0");
        Is(e, "stopwatchstart[`SW:77`]", "SW:77");
        // A handle typed straight in is cut at the ':' and names no stopwatch.
        Is(e, "stopwatchstart[SW:0]", "SW");
    }

    private static void TypesOptionsUnknown()
    {
        FakeHost.Reset();
        var e = Engine(FakeHost.Create());
        Is(e, "getobjectinternaltype[``]", "0");
        Is(e, "getobjectinternaltype[1.5]", "1");
        Is(e, "getobjectinternaltype[abc]", "3");
        Is(e, "getobjectinternaltype[stopwatchcreate[]]", "7");
        Is(e, "getobjectinternaltype[dictcreate[]]", "7");
        Is(e, "getobjectinternaltype[`[WorldObject] 0x50000001: Sword`]", "7");
        Is(e, "getobjectinternaltype[listcreate[1,2]]", "3");   // lists are text

        Is(e, "raoptget[nope]", "0");
        Is(e, "raoptset[mode, hunt]", "1");
        Is(e, "raoptget[MODE]", "hunt");
        Is(e, "uboptget[mode]", "hunt");
        Is(e, "uboptset[mode, loot]", "1");
        Is(e, "raoptget[mode]", "loot");
        Is(e, "raoptset[``, x]", "0");
        e.RegisterOption("registered", "5");
        e.RegisterOption("registered", "6");       // the first registration wins
        Is(e, "raoptget[registered]", "5");

        Is(e, "nosuchfn[1]", "");
        Is(e, "nosuchfn[2]", "");
        Is(e, "NoSuchFn[3]", "");
        int logged = FakeHost.Logs.FindAll(l => l.Contains("unknown expression function 'nosuchfn")).Count;
        Check.Eq(logged, 1, "an unknown function is logged once per name (names ignore case)");
    }

    private static void SettingsAndState()
    {
        var noSettings = Engine();
        Is(noSettings, "vtgetsetting[EnableCombat]", "");
        Is(noSettings, "vtsetsetting[EnableCombat, 1]", "0");
        Is(noSettings, "setmetastate[Hunt]", "0");
        Is(noSettings, "getmetastate[]", "");

        var s = new LegacyUiSettings { EnableCombat = true, MonsterRange = 30 };
        var e = Engine(settings: s);
        Is(e, "vtgetsetting[enablecombat]", "1");
        Is(e, "vtsetsetting[EnableCombat, 0]", "1");
        Check.False(s.EnableCombat, "EnableCombat off");
        Is(e, "ragetsetting[MonsterRange]", "30");
        Is(e, "rasetsetting[MonsterRange, 45]", "1");
        Check.Eq(s.MonsterRange, 45, "MonsterRange set");
        Is(e, "rasetsetting[MonsterRange, far]", "1");
        Check.Eq(s.MonsterRange, 45, "unparsable int leaves the value");
        Is(e, "vtsetsetting[NavOffTrackYards, 100]", "1");
        Check.Near(s.NavOffTrackYards, 160, 1e-6, "NavOffTrackYards clamps to at least 160");
        Is(e, "vtsetsetting[AutoVendorTries, 99]", "1");
        Check.Eq(s.AutoVendorTries, 20, "AutoVendorTries clamps to 20");
        Is(e, "vtsetsetting[BowArcVelocity, NaN]", "1");
        Check.True(float.IsFinite(s.BowArcVelocity), "NaN is refused");
        Is(e, "vtsetsetting[NotASetting, 7]", "1");
        Is(e, "vtgetsetting[NotASetting]", "7");        // unknown names become options
        Is(e, "raoptget[NotASetting]", "7");
        Is(e, "vtgetsetting[``]", "");

        s.CurrentState = "Default";
        s.ForceStateReset = false;
        Is(e, "setmetastate[Hunt]", "1");
        Check.Eq(s.CurrentState, "Hunt", "state set");
        Check.True(s.ForceStateReset, "state change asks for a reset");
        Is(e, "getmetastate[]", "Hunt");
        Is(e, "vtsetmetastate[`Two Words`]", "1");
        Check.Eq(s.CurrentState, "Two Words", "state names may have spaces (raw argument)");
        Is(e, "setmetastate[``]", "0");
        s.CurrentMetaPath = @"X:\metas\Conquest 10.af";
        Is(e, "vtgetmeta[]", "Conquest 10");
    }

    private static void CharacterFunctions()
    {
        FakeHost.Reset();
        FakeHost.Ints[(Player, 25)] = 275;
        FakeHost.Ints[(Player, 5)] = 1500;           // burden
        FakeHost.Doubles[(Player, 7)] = 0.5;
        FakeHost.Quads[(Player, 1)] = 123456789012L;
        FakeHost.Bools[(Player, 3)] = true;
        FakeHost.Strings[(Player, 1)] = "Test Toon";
        FakeHost.Skills[(Player, 34)] = (Buffed: 400, Training: 3);
        FakeHost.SkillLevels[(Player, 34, 0)] = 420;
        FakeHost.SkillLevels[(Player, 34, 1)] = 300;
        FakeHost.Attributes[(Player, 1, 0)] = 100;   // strength buffed
        FakeHost.Attributes[(Player, 1, 1)] = 90;    // strength base
        FakeHost.Vitals = (Hp: 150, MaxHp: 200, Stam: 250, MaxStam: 300, Mana: 50, MaxMana: 400);
        FakeHost.BaseVitals = (Hp: 180, Stam: 280, Mana: 380);
        FakeHost.KnownSpells.Add(1332);
        FakeHost.Vitae = 0.95f;
        var host = FakeHost.Create();
        var e = Engine(host, playerId: Player);

        Is(e, "getcharintprop[25]", "275");
        Is(e, "getcharintprop[26]", "0");
        Is(e, "getcharintprop[abc]", "0");
        Is(e, "getchardoubleprop[7]", "0.5");
        Is(e, "getcharquadprop[1]", "123456789012");
        Is(e, "getcharboolprop[3]", "1");
        Is(e, "getcharboolprop[4]", "0");
        Is(e, "getcharstringprop[1]", "Test Toon");
        Is(e, "getcharstringprop[2]", "");
        Is(e, "getcharskill_traininglevel[34]", "3");
        Is(e, "getcharskill_buffed[34]", "420");
        Is(e, "getcharskill_base[34]", "300");
        Is(e, "getcharskill_buffed[0]", "0");
        Is(e, "getcharattribute_buffed[1]", "100");
        Is(e, "getcharattribute_base[1]", "90");
        Is(e, "getcharburden[]", "10");                 // 1500 / (100 strength * 150)
        Is(e, "getcharburden_total[]", "1500");
        Is(e, "getcharvital_current[1]", "150");
        Is(e, "getcharvital_current[2]", "250");
        Is(e, "getcharvital_current[3]", "50");
        Is(e, "getcharvital_current[4]", "0");
        Is(e, "getcharvital_base[1]", "180");
        Is(e, "getcharvital_base[3]", "380");
        Is(e, "getcharvital_buffedmax[1]", "0");        // not faked: 0
        Is(e, "getisspellknown[1332]", "1");
        Is(e, "getisspellknown[1333]", "0");
        Is(e, "getcancastspell_hunt[1332]", "1");
        Is(e, "getcancastspell_buff[1333]", "0");
        FakeHost.Vitals = (Hp: 150, MaxHp: 200, Stam: 250, MaxStam: 300, Mana: 0, MaxMana: 400);
        Is(e, "getcancastspell_hunt[1332]", "0");       // no mana
        Is(e, "vitae[]", "5");

        var noPlayer = Engine(host);
        Is(noPlayer, "getcharintprop[25]", "0");
        Is(noPlayer, "getcharskill_buffed[34]", "0");
        Is(noPlayer, "getcharburden[]", "0");
        var nullHost = Engine(default, playerId: Player);
        Is(nullHost, "getcharintprop[25]", "0");
        Is(nullHost, "getcharvital_current[1]", "0");
        Is(nullHost, "getisspellknown[1332]", "0");
    }

    private static void Coordinates()
    {
        FakeHost.Reset();
        FakeHost.PlayerPose = (0xA9B4001Cu, 96f, 96f, 48f);
        FakeHost.WorldName = "Aelrynth";
        FakeHost.AccountName = "tester";
        var e = Engine(FakeHost.Create(), playerId: Player);

        Is(e, "getplayerlandcell[]", "2847146012");
        Is(e, "getplayerlandblock[]", "2847145984");
        Is(e, "getcellid[]", "0xA9B4001C");
        Is(e, "getworldname[]", "Aelrynth");
        Is(e, "getaccounthash[]", "f5d1278e8109edd94e1e4197e04873b9");   // md5("tester")
        e.Evaluate("setvar[here, getplayercoordinates[]]");
        Check.Near(double.Parse(e.Evaluate("coordinategetns[$here]"), System.Globalization.CultureInfo.InvariantCulture), 42.45, 1e-9, "NS from the landcell");
        Check.Near(double.Parse(e.Evaluate("coordinategetwe[$here]"), System.Globalization.CultureInfo.InvariantCulture), 33.65, 1e-9, "EW from the landcell");
        Check.Near(double.Parse(e.Evaluate("coordinategetz[$here]"), System.Globalization.CultureInfo.InvariantCulture), 0.2, 1e-9, "Z in coordinate units (z / 240)");
        Is(e, "coordinatetostring[$here]", "42.45N, 33.65E, 0.20Z");

        Is(e, "coordinateparse[42.45N, 33.65W]", "42.45|-33.65|0");
        Is(e, "coordinateparse[`12.3S 4.5E, 1.5Z`]", "-12.3|4.5|1.5");
        Is(e, "coordinateparse[nowhere]", "");
        Is(e, "coordinatetostring[`-1.5|-2.25|0`]", "1.50S, 2.25W, 0.00Z");
        Is(e, "coordinatetostring[garbage]", "");
        Is(e, "coordinategetns[garbage]", "0");

        e.Evaluate("setvar[a, coordinateparse[0N, 0E]]");
        e.Evaluate("setvar[b, coordinateparse[1N, 0E, 1Z]]");
        Is(e, "coordinatedistanceflat[$a, $b]", "240");
        Check.Near(double.Parse(e.Evaluate("coordinatedistancewithz[$a, $b]"), System.Globalization.CultureInfo.InvariantCulture), Math.Sqrt(2) * 240, 1e-9, "with Z");
        Is(e, "coordinatedistanceflat[$a, junk]", "0");
        // A coordinate written straight into an expression is read as bitwise OR (1|2|3 = 3, not a
        // coordinate), so pass it in a variable or backticks.
        Is(e, "coordinategetns[1|2|3]", "0");
        Is(e, "coordinategetns[`1|2|3`]", "1");

        FakeHost.HasPose = false;
        Is(e, "getplayercoordinates[]", "");
        Is(e, "getcellid[]", "pose_failed");
        var nullHost = Engine();
        Is(nullHost, "getcellid[]", "unavailable");
        Is(nullHost, "getplayerlandcell[]", "0");
        Is(nullHost, "isportaling[]", "0");
    }

    private static void SpellExpiration()
    {
        FakeHost.Reset();
        FakeHost.ServerTime = 1000;
        FakeHost.Enchantments.Add((1332, 1100.5));   // Strength Self VI, 100.5 s left
        FakeHost.Enchantments.Add((1337, 900));      // Strength Other VI, expired
        FakeHost.Enchantments.Add((2, 1e12));        // effectively permanent
        var e = Engine(FakeHost.Create(), playerId: Player);

        Is(e, "getspellexpiration[1332]", "101");     // rounded up
        Is(e, "getspellexpiration[1337]", "0");
        Is(e, "getspellexpiration[2]", "2147483647");
        Is(e, "getspellexpiration[5]", "0");
        Is(e, "getspellexpiration[abc]", "0");
        Is(e, "getspellexpirationbyname[`Self VI`]", "101");
        Is(e, "getspellexpirationbyname[`strength other`]", "0");
        Is(e, "getspellexpirationbyname[`No Such Spell`]", "0");
        Is(e, "getspellexpirationbyname[``]", "-1");
        FakeHost.ServerTime = 0;
        Is(e, "getspellexpiration[1332]", "0");       // server time unknown
        Is(e, "getspellexpirationbyname[Strength]", "-1");
        var nullHost = Engine();
        Is(nullHost, "getspellexpiration[1332]", "0");
        Is(nullHost, "getspellexpirationbyname[Strength]", "-1");
    }

    private static void GameTime()
    {
        FakeHost.Reset();
        var e = Engine(FakeHost.Create(), playerId: Player);   // no GetPlayerId: the clock reads 0
        // With a raw clock of 0 the tick count is -210 + 476.25*8 + 476.25*16*30*12*10 = 27435600.
        Is(e, "getgameticks[]", "27435600");
        Is(e, "getgameyear[]", "10");
        Is(e, "getgamemonth[]", "0");
        Is(e, "getgameday[]", "0");
        Is(e, "getgamehour[]", "7");
        Is(e, "getisday[]", "1");
        Is(e, "getisnight[]", "0");
        Is(e, "getminutesuntilnight[]", "35");
        Is(e, "getminutesuntilday[]", "0");
        Is(e, "getgamemonthname[0]", "Morningthaw");
        Is(e, "getgamemonthname[11]", "Wintersebb");
        Is(e, "getgamemonthname[12]", "");
        Is(e, "getgamemonthname[x]", "");
        Is(e, "getgamehourname[0]", "Darktide");
        Is(e, "getgamehourname[15]", "Gloaming-and-Half");
        int logged = FakeHost.Logs.FindAll(l => l.Contains("before world entry")).Count;
        Check.Eq(logged, 1, "the not-in-world note is logged once");
        string year = e.Evaluate("getdatetimelocal[yyyy]");
        Check.Eq(year, DateTime.Now.Year.ToString(), "getdatetimelocal formats the local time");
        long unix = long.Parse(e.Evaluate("getunixtime[]"));
        Check.True(Math.Abs(unix - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) < 5, "getunixtime is now");
    }

    private static void Quests()
    {
        var none = Engine();
        Is(none, "testquestflag[blankaug]", "0");
        Is(none, "getqueststatus[blankaug]", "1");     // no data: assume ready
        Is(none, "getquestktprogress[x]", "0");
        Is(none, "refreshquests[]", "1");
        Is(none, "isrefreshingquests[]", "0");

        FakeHost.Reset();
        var host = FakeHost.Create();
        var qt = new QuestTracker(host);
        var e = Engine(host);
        e.SetQuestTracker(qt);
        Is(e, "refreshquests[]", "1");
        Check.True(FakeHost.ChatCommands.Contains("/myquests"), "asks the server with /myquests");
        Is(e, "isrefreshingquests[]", "1");
        long recent = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
        qt.OnChatLine("blankaug - 1 solves (1609459200)\"Blank Aug\" 1 0");
        qt.OnChatLine($"killtask - 12 solves ({recent})\"Kill task\" 50 3600");
        qt.OnChatLine("oldtimer - 3 solves (1609459200)\"Old timer\" 10 3600");
        qt.OnChatLine("You say, \"hello\"");
        Is(e, "testquestflag[BlankAug]", "1");
        Is(e, "testquestflag[nothere]", "0");
        Is(e, "getqueststatus[blankaug]", "0");        // once-only, done: never ready again
        Is(e, "getqueststatus[killtask]", "0");        // repeat timer still running
        Is(e, "getqueststatus[oldtimer]", "1");        // timer long expired
        Is(e, "getqueststatus[nothere]", "1");
        Is(e, "getquestktprogress[killtask]", "12");
        Is(e, "getquestktrequired[killtask]", "50");
        qt.OnChatLine("Quest list is empty.");
        Is(e, "isrefreshingquests[]", "0");
    }

    private static void FellowshipDefaults()
    {
        var e = Engine();
        Is(e, "getfellowshipstatus[]", "0");
        Is(e, "getfellowshipcount[]", "0");
        Is(e, "getfellowshipname[]", "");
        Is(e, "getfellowshipisleader[]", "0");
        Is(e, "getfellowshipisfull[]", "0");
        Is(e, "getfellowshipcanrecruit[]", "0");
        Is(e, "getfellowid[0]", "0");
        Is(e, "getfellowname[0]", "");
        Is(e, "getfellownames[]", "[]");
        Is(e, "getfellowids[]", "[]");
    }

    private static void WorldObjects()
    {
        FakeHost.Reset();
        const uint kitA = 0x60000001, kitB = 0x60000002, stone = 0x60000003;
        const uint drudge = 0x80000011, farDrudge = 0x80000012, npc = 0x80000013;
        FakeHost.Names[kitA] = "Healing Kit";
        FakeHost.Names[kitB] = "Healing Kit";
        FakeHost.Names[stone] = "Mana Stone";
        FakeHost.Ints[(kitA, 12)] = 5;
        FakeHost.Ints[(kitB, 12)] = 3;
        FakeHost.Ints[(kitA, 19)] = 250;
        FakeHost.Positions[Player] = (0xA9B4001Cu, 50f, 50f, 0f);
        foreach (var (id, name, x) in new[] { (drudge, "Drudge Skulker", 60f), (farDrudge, "Drudge Ravener", 150f), (npc, "Town Crier", 51f) })
        {
            FakeHost.Names[id] = name;
            FakeHost.Positions[id] = (0xA9B4001Cu, x, 50f, 0f);
            FakeHost.ItemTypes[id] = 0x10;   // creature
        }
        FakeHost.NotAttackable.Add(npc);
        var host = FakeHost.Create();
        var cache = FakeHost.MakeCache(host, Player, new[] { kitA, kitB, stone, drudge, farDrudge, npc },
            new Dictionary<uint, float> { [drudge] = 1f, [farDrudge] = 1f, [npc] = 1f });
        var e = Engine(host, playerId: Player);
        e.SetObjectCache(cache);

        Is(e, "wobjectfindininventorybyname[Healing Kit]", "[WorldObject] 0x60000001: Healing Kit");
        Is(e, "wobjectfindininventorybyname[healing kit]", "[WorldObject] 0x60000001: Healing Kit");
        Is(e, "wobjectfindininventorybyname[Healing]", "0");
        Is(e, "wobjectfindininventorybynamerx[`^mana`]", "[WorldObject] 0x60000003: Mana Stone");
        Is(e, "listcount[wobjectfindallinventorybynamerx[Kit]]", "2");
        Is(e, "getitemcountininventorybyname[Healing Kit]", "8");        // stack sizes summed
        Is(e, "getitemcountininventorybynamerx[`heal`]", "8");
        Is(e, "getitemcountininventorybyname[Mana Stone]", "1");
        Is(e, "getitemcountininventorybyname[Nothing]", "0");

        e.Evaluate("setvar[kit, wobjectfindininventorybyname[Healing Kit]]");
        Is(e, "wobjectgetname[$kit]", "Healing Kit");
        Is(e, "wobjectgetid[$kit]", "1610612737");
        Is(e, "wobjectgetintprop[$kit, 19]", "250");
        Is(e, "wobjectgetintprop[0x60000001, 12]", "5");                 // a hex literal works too
        Is(e, "wobjectgetintprop[1610612737, 12]", "5");                 // and a decimal id
        Is(e, "wobjectgetintprop[$kit, 999]", "0");
        Is(e, "wobjectgetintprop[0, 12]", "0");
        Is(e, "wobjectgetname[0x60000002]", "Healing Kit");              // name looked up from the host
        Is(e, "wobjectgetname[junk]", "");
        Is(e, "wobjectfindbyid[0x60000003]", "[WorldObject] 0x60000003: Mana Stone");
        Is(e, "wobjectfindbyid[12345]", "0");

        Is(e, "wobjectfindnearestmonster[]", "[WorldObject] 0x80000011: Drudge Skulker");   // the NPC is closer but not attackable
        Is(e, "wobjectgetobjectclass[wobjectfindnearestmonster[]]", "5");
        Is(e, "wobjectgetobjectclass[0x80000013]", ((int)AcObjectClass.Npc).ToString());
        Is(e, "listcount[wobjectfindallbynamerx[`^Drudge`]]", "2");

        var noCache = Engine(host, playerId: Player);
        Is(noCache, "wobjectfindininventorybyname[Healing Kit]", "0");
        Is(noCache, "getitemcountininventorybyname[Healing Kit]", "0");
        Is(noCache, "wobjectfindnearestmonster[]", "0");
        Is(noCache, "wobjectfindall[]", "[]");
    }

    private static void ChatFunctions()
    {
        FakeHost.Reset();
        var e = Engine(FakeHost.Create());
        Is(e, "chatbox[`/say hi`]", "/say hi");
        Check.True(FakeHost.ChatCommands.Contains("/say hi"), "chatbox goes to the chat parser");
        Is(e, "echo[`note to self`, 5]", "1");
        Check.True(FakeHost.Chat.Contains("note to self"), "echo writes to the chat window");
        Is(e, "chatboxpaste[x]", "0");
        var nullHost = Engine();
        Is(nullHost, "chatbox[`/say hi`]", "/say hi");
        Is(nullHost, "echo[x, 1]", "0");
    }

    // ── Bug tests (fixed unless still registered with KnownFailure) ───────────

    private static void DollarMatchesGetvar()
    {
        var e = Engine();
        Check.Eq(e.Evaluate("$unset"), e.Evaluate("getvar[unset]"), "$unset vs getvar[unset]");
        Check.Eq(e.Evaluate("$unset == 0"), "1", "$unset == 0");
    }

    private static void NegativeZeroIsFalse()
    {
        var e = Engine();
        Check.False(ExpressionEngine.ToBool(e.Evaluate("ceiling[-0.5]")), $"ceiling[-0.5] is '{e.Evaluate("ceiling[-0.5]")}'");
        Check.False(ExpressionEngine.ToBool(e.Evaluate("0*-1")), $"0*-1 is '{e.Evaluate("0*-1")}'");
        Check.Eq(e.Evaluate("round[-0.4]"), "0", "round[-0.4]");
    }

    private static void ListItemsWithCommas()
    {
        var e = Engine();
        Check.Eq(e.Evaluate("listcount[listcreate[`a,b`, c]]"), "2", "an item holding a comma");
        Check.Eq(e.Evaluate("listcount[listadd[listcreate[a], listcreate[b,c]]]"), "2", "a list added to a list");
    }

    private static void RegexOperatorUnquoted()
    {
        var e = Engine();
        e.Evaluate("setvar[name, `Virindi Director`]");
        e.Evaluate("setvar[lastchat, `You killed the olthoi!`]");
        Check.Eq(e.Evaluate("getvar[name] # ^Virindi"), "1", "getvar[name] # ^Virindi");
        Check.Eq(e.Evaluate("getvar[lastchat] # killed.*olthoi"), "1", "getvar[lastchat] # killed.*olthoi");
    }

    private static void VitaeWithoutHost()
    {
        Check.Eq(Engine().Evaluate("vitae[]"), "0", "no host: no known penalty");
        FakeHost.Reset();
        Check.Eq(Engine(FakeHost.Create()).Evaluate("vitae[]"), "0", "no player id: no known penalty");
    }
}
