namespace SkarpSharp.Findings;

/// <summary>One God Class rule set: WMC at least (or over) a value, ATFD over a value, TCC below one third.</summary>
public sealed record GodClassSet(string Id, int Wmc, bool WmcStrict, int Atfd, string Source, string Evidence)
{
    public string Describe() => $"WMC {(WmcStrict ? "over" : "at least")} {Wmc}, ATFD over {Atfd}, TCC below 1/3";
}

/// <summary>The God Class rule sets, the values of the rule's <c>preset</c> parameter. They live with the
/// rule catalog, which validates the parameter; <c>Semantic.GodClassRule</c> applies them.</summary>
public static class GodClassPresets
{
    const string Secondhand = "secondhand (book not read)";

    public static readonly IReadOnlyList<GodClassSet> Sets =
    [
        new("lanza-marinescu-2006", 47, false, 5, "Lanza and Marinescu 2006: Java values, via PMD GodClassRule", Secondhand),
        new("iplasma", 20, true, 4, "iPlasma God Class: WMC over 20, ATFD over 4, TCC below 1/3", Secondhand),
        new("atfd-2", 47, false, 2, "Lanza and Marinescu 2006 with FEW at 2: the ATFD over 2 variant, also used by Clean-CaDET", Secondhand),
    ];

    public static GodClassSet? Find(string id) => Sets.FirstOrDefault(s => s.Id == id);
}
