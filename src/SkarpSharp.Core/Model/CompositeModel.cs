using System.Text.Json.Serialization;

namespace SkarpSharp.Model;

// Output model (additive, no schema bump): the composites block, present only when brain-method
// or brain-class is switched on (experimental). The part findings stay in findings[] unchanged.

/// <summary>
/// A composite finding (<c>brain-method</c>, <c>brain-class</c>): one place that stands for several findings at it, its
/// parts. Reports show it as one line in place of its parts; baseline, gate and score work on the parts only.
/// </summary>
public sealed class CompositeFinding
{
    /// <summary><c>rule|path|place</c> as for findings (section 4 of contracts.md): the member key or type name of its anchor.</summary>
    public string Id { get; set; } = "";
    public string? Handle { get; set; }
    /// <summary><c>brain-method</c> or <c>brain-class</c>.</summary>
    public string Rule { get; set; } = "";
    /// <summary>The highest severity among the parts.</summary>
    public string Severity { get; set; } = "";
    public string Path { get; set; } = "";
    public int Line { get; set; }
    /// <summary>Member id (brain-method) or type name (brain-class), as the anchor finding names it.</summary>
    public string? Member { get; set; }
    public string Message { get; set; } = "";
    /// <summary>Derived from the parts against the baseline: <c>known</c> when every part is known, <c>new</c> when every
    /// part is new, <c>worsened</c> otherwise (a part new or worse); absent without a baseline.</summary>
    public string? BaselineState { get; set; }
    /// <summary>The ids of the part findings (for a brain class: its <c>large-type</c> finding, then each brain method's parts).</summary>
    public List<string> Parts { get; set; } = [];
    /// <summary>Brain class only: the ids of its brain methods (composite ids, also when brain-method is off).</summary>
    public List<string>? BrainMethods { get; set; }
    /// <summary>Member key or type name of the place (identity part); not serialized.</summary>
    [JsonIgnore] public string? PlaceKey { get; set; }
}
