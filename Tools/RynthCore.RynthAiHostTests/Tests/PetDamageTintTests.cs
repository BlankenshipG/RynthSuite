using RynthCore.Plugin.RynthAi.IltHub;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Pets roster tint (2026-10-12): every essence showed white because ILT essences mostly carry
// neither the captured damage type (9054) nor DamageType (45). As UB-ILT (PetDamageTypeTintPolicy,
// v2.0.1119), the essence name gives the school then, and an elemental name beats a physical-only
// 9054 (ILT reuses that id for the min-summon aug, where 1/2/4 read as Slash/Pierce/Bludgeon).
internal static class PetDamageTintTests
{
    public static void Register(Runner r)
    {
        r.Add("pet tint: the school comes from the properties, else the essence name (UB-ILT v2.0.1119)", PickSchool);
        r.Add("pet tint: the palette is UB-ILT's Leaftide damage-type colours", Palette);
    }

    private static void PickSchool()
    {
        string N(int captured, int mask, string name) => IltDamageTypeTint.Name(IltDamageTypeTint.PickPetFlag(captured, mask, name));

        Check.Eq(N(0, 0, "Fire Banshee Essence (300)"), "Fire", "no properties: the name says Fire");
        Check.Eq(N(0, 0, "Acid Phyntos Swarm Essence (250)"), "Acid", "Acid");
        Check.Eq(N(0, 0, "Blizzard Wisp Essence (250)"), "Cold", "a Blizzard is Cold");
        Check.Eq(N(0, 0, "Incendiary Knight Essence"), "Fire", "an ILT synonym");
        Check.Eq(N(0, 0, "Shocked Grievver Essence"), "Lightning", "Lightning synonym");
        Check.Eq(N(0, 0, "Novice Essence"), "", "'ice' inside a word doesn't count");
        Check.Eq(N(0, 0, "Tremendous Monouga Essence"), "", "no school in the name: unknown");
        Check.Eq(N(1024, 0, "Fire Banshee Essence"), "Nether", "an elemental 9054 wins over the name");
        Check.Eq(N(1, 0, "Fire Banshee Essence"), "Fire", "a physical-only 9054 loses to an elemental name");
        Check.Eq(N(4, 0, "Monouga Essence"), "Bludgeon", "... but stands when the name says nothing");
        Check.Eq(N(0, 32, ""), "Acid", "the DamageType mask when 9054 is missing");
        Check.Eq(N(0, 1024 | 1, ""), "Nether", "a combined mask: elements before physical");
        Check.Eq(N(0, 3, ""), "Slash/Pierce", "Slash/Pierce is its own school");
    }

    private static void Palette()
    {
        static void Rgb(string school, int r, int g, int b)
        {
            var c = IltDamageTypeTint.ForName(school);
            Check.Near(c.X, r / 255f, 1e-4, school + " red");
            Check.Near(c.Y, g / 255f, 1e-4, school + " green");
            Check.Near(c.Z, b / 255f, 1e-4, school + " blue");
        }
        Rgb("Slash", 255, 180, 120);
        Rgb("Pierce", 240, 200, 140);
        Rgb("Bludgeon", 220, 190, 140);
        Rgb("Cold", 160, 210, 255);
        Rgb("Fire", 255, 130, 70);
        Rgb("Acid", 160, 255, 140);
        Rgb("Lightning", 255, 240, 120);
        Rgb("Nether", 200, 160, 255);
        Check.Eq(IltDamageTypeTint.ForName(""), IltDamageTypeTint.Unknown, "unknown: white");
    }
}
