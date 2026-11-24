# Conditional compilation

Skarp analyzes a file with `#if` directives several times, once for each planned set of preprocessor symbols, and merges the results, so code inside `#if` branches is measured and not silently skipped.

## Why this matters

A C# parser sees only the branches that are active for one set of symbols. With no symbols set, a file such as this one shows the `#else` branch only:

```csharp
#if NET8_0_OR_GREATER
    // new code
#elif NET48
    // legacy code
#else
    // fallback
#endif
```

Code in the other branches would never be measured, and a complex method hidden behind `NET48` would never be reported. Skarp avoids that by planning a small set of symbol sets that, together, activate as many branches as possible, and by parsing the file under each.

This works in the syntax tier, and needs no build and no project evaluation.

## How a file is analyzed

For each file that has an `#if`:

1. **Base view.** The file is parsed once as it is, with no symbols defined except its own `#define` lines. The base view gives the primary values in the JSON output.
2. **Plan.** Skarp reads the directives and plans the variants (below). Whether a branch is active in a variant is worked out by Skarp's own evaluator, which follows Roslyn's rules and is tested against Roslyn.
3. **Parse each variant.** Every variant is a full parse of the file's text with the variant's symbols. A variant that has more syntax errors than the base view is discarded as an unreal mix. It is counted and not merged.
4. **Merge.** Members are matched across variants by the position of their identifier, their kind and their name. For each metric the worst value over the base view and all variants is kept. Type and file sizes take the largest single variant. A member that exists only in a variant is added.

A file with unbalanced directives (for example an `#if` without `#endif`) gets no variants and is analyzed as it is.

## How variants are planned

The planner uses only the names in the file's own conditions.

- **Framework symbols.** Names such as `NET48`, `NETSTANDARD2_0`, `NETCOREAPP3_1`, `NET8_0` and their `_OR_GREATER` forms stand for a target framework. Skarp uses the symbols the .NET SDK defines for each framework, so a variant for `net8.0` also has `NET`, `NET8_0_OR_GREATER`, `NET7_0_OR_GREATER` and so on. A family name without a version, such as `NET`, maps to the newest known framework of that family. `TRACE` is on in every framework variant.
- **Which frameworks.** If the file mentions three or fewer frameworks, each gets a variant. With more, Skarp picks the lowest, the highest, the last .NET Framework and the first .NET Core or .NET one. Branches that none of these activates get extra variants, then the newest known framework that activates them.
- **Other symbols** (such as `FEATURE_X`) are switched on in the existing variants where they can be, and in extra variants for the branches that remain.
- **`DEBUG` and `RELEASE`** are never on together. At most one platform OS symbol is on in a variant.
- **Unused symbols are not switched on.** An other symbol that appears in no project file under the repository (`.csproj`, `.props`, `.targets`, including `Directory.Build.props` above the analyzed folder) is left off, because nothing in the build could define it. This is a plain text scan of those files, not an MSBuild evaluation. The run reports how many symbols were left off.
- **Branches that cannot be active** (`#if false`, a contradiction with an enclosing condition, or a condition that needs two frameworks or two platforms) are listed as unreachable and never reported as findings.

The plan is deterministic: the same file and settings give the same variants.

### Choosing the symbols yourself

If you know which symbol sets matter, set them in `.skarp.json`:

```json
{
  "symbols": ["NET48", "NET10_0", "DEBUG"]
}
```

With `symbols` set, automatic planning is replaced. For each file, Skarp combines the listed symbols the file uses: frameworks one at a time, other symbols on and off. A listed symbol is never filtered out. `"symbolsMode": "append"` adds to the list of a lower settings level instead of replacing it. See [settings](settings.md).

## The cap

Each variant is one more parse, so the number of variants per file is capped.

| Setting | Default | Meaning |
| --- | --- | --- |
| `variants.cap` | 32 | Variants per file besides the base view, for `analyze`, `baseline` and `hotspots`. `0` analyzes the base view only. |
| `variants.checkCap` | 4 | Variants per file for `skarp check`, with the base view counted in. `1` means the base view only. |

When a plan is longer than the cap, the lowest-ranked variants are dropped. The ranking is: framework variants first (highest, then lowest, then upward from the lowest), then extra framework variants, then variants that cover other symbols.

Set them in `.skarp.json` (`"variants": { "cap": 32, "checkCap": 4 }`) or for one run with `--setting variants.cap=8`. Few files need more than a handful of variants.

## What the report says

The run header summarizes the planning. On a file with `#if` and the default settings:

```
variants:   1 file with #if, 2 variants parsed (covering set, cap 32); 1 unreachable branch (--all lists them)
```

`covering set` means automatic planning. With the `symbols` setting it says `symbols setting`. When the cap cuts a plan, the line says so, and `--all` lists the files:

```
variants:   1 file with #if, 2 variants parsed (covering set, cap 2); 1 capped, 4 branches not planned (--all lists them)
#if variants
  capped  src/Many.cs: 2 variants kept of more planned (variants.cap 2)
```

The parts of the line mean:

| Part | Meaning |
| --- | --- |
| `N capped` | Files whose plan was cut at the cap. |
| `N discarded by the syntax-error guard` | Variants dropped for having more syntax errors than the base view. |
| `N unreachable branches` | Branches no symbol set can activate. `--all` lists each with its line, the directive and the reason (`never true` or `exclusive`). |
| `N symbols in no project file left off` | Symbols not switched on because no project file mentions them. |
| `N branches not planned` | Branches left inactive: they need a left-off symbol, a symbol your `symbols` setting leaves off, or a variant the cap cut. |

If you see `capped` or `not planned`, some code under `#if` was not measured. Raise `variants.cap`, or list the symbols you care about with `symbols`.

### Findings from a variant

A finding is normally the worst value over all variants. When a finding comes from a variant and not from the base view, the JSON marks it with `variant` (for example `NET8_0+DEBUG`) and its message says so:

```
cognitive complexity 16 (with NET8_0 and DEBUG on)
```

A finding that only a variant shows is a notice. Variant names are the shortest form: the framework's own symbol plus the symbols switched on, for example `NET8_0+DEBUG`. The JSON of each file lists its variants and the symbols each was parsed with (`files[].variants`), and the run header has the `run.variants` block. This block is experimental; see [contracts](contracts.md).

## Other tiers

- The [semantic tier](semantic-tier.md) compiles each project for a target framework with the project's real defined symbols, so code in `#if` branches that are inactive there is not evaluated. For the unused-code rules, a member whose name appears in inactive `#if` code is not reported as unused.
- With [coverage](coverage.md), a member whose span holds code inactive in every planned variant is never reported as untested, and covered lines in such code are counted in a warning.
- `skarp check` (experimental) uses the smaller `variants.checkCap` so that it stays fast after each edit.

## See also

- [Settings](settings.md)
- [Rules](rules.md)
- [Semantic tier](semantic-tier.md)
- [Coverage](coverage.md)
- [Contracts](contracts.md)
