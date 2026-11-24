# Microsoft metrics oracle

Compares `skarp analyze` output with Microsoft's code-metrics engine (the NuGet package `Microsoft.CodeAnalysis.Metrics`, MIT; the engine behind Visual Studio "Calculate Code Metrics" and the CA1502 rule), member by member.

Skarp's `microsoft` counting mode (`cyclomaticMicrosoft`, `cboMicrosoft`) aims to match this engine. The oracle shows where it does and does not. You only need to run it after changing a counting rule or adding a construct to the edge cases. Every `dotnet test` already checks the recorded values (section 5); the Microsoft engine is not needed for that.

Inputs:

- `tests/oracle/EdgeCases/`: one member per tricky C# construct, written for this oracle.
- `tests/oracle/Coupling/`: worked examples for class coupling, one namespace per example.

Generated files (XML, JSON, diffs) are not committed. Work in a scratch folder outside the repository.

## 1. Get a working Metrics.exe

The packages `Microsoft.CodeAnalysis.Metrics` 5.0.0, 5.3.0 and 5.6.0 ship `Metrics/Metrics.exe` without the `BuildHost-netcore` folder its MSBuildWorkspace needs, so they stop with "The build host could not be found". Version 4.14.0 runs but bundles an old compiler that cannot parse C# 12 and later. This workaround was used for 5.6.0 (its `Metrics.exe` bundles Roslyn 4.12.0-3.24572.7). Check first whether a newer package version has fixed the problem.

1. Restore both packages into the NuGet cache, for example with a throwaway project in the scratch folder:
   ```xml
   <ItemGroup>
     <PackageDownload Include="Microsoft.CodeAnalysis.Metrics" Version="[5.6.0]" />
     <PackageDownload Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="[4.12.0]" />
   </ItemGroup>
   ```
2. Copy `~/.nuget/packages/microsoft.codeanalysis.metrics/5.6.0/Metrics/` to `<scratch>/metrics-5.6.0/`. Then copy `BuildHost-net472/` and `BuildHost-netcore/` from `~/.nuget/packages/microsoft.codeanalysis.workspaces.msbuild/4.12.0/contentFiles/any/any/` into it. Do not patch the NuGet cache itself.

The documented route (a `PackageReference` to `Microsoft.CodeAnalysis.Metrics` and `dotnet build -t:Metrics`) runs the same exe from the cache and fails the same way until the package is fixed.

## 2. Run Microsoft's engine

Copy `tests/oracle/EdgeCases` to the scratch folder first, so `bin` and `obj` stay out of the repository.

```sh
dotnet build <scratch>/EdgeCases -c Release
<scratch>/metrics-5.6.0/Metrics.exe /project:<scratch>\EdgeCases\EdgeCases.csproj /out:<scratch>\ec-ms.xml
```

## 3. Run Skarp

```sh
dotnet run --project src/SkarpSharp.Cli -c Release -- analyze <scratch>/EdgeCases --no-history -o <scratch>/ec-ours.json
```

Run it on the same scratch copy: matching uses the file paths Microsoft's engine reports.

## 4. Compare

```sh
node tools/oracle/compare-ms-metrics.mjs <scratch>/ec-ms.xml <scratch>/ec-ours.json --diffs <scratch>/ec-diffs.json --show 20
```

The script prints how many members matched and, per metric pair, how many matched members are equal, a histogram of the difference (Microsoft minus Skarp) and the first differences. `--diffs` writes every difference and the unmatched members as JSON.

Metric pairs:

- `CyclomaticComplexity` against `cyclomaticMicrosoft`. The target is equal values.
- `ExecutableLines` against `statements` (and against `statements` folded: a member's own count plus all nested lambda and local-function records). The definitions differ by design; the comparison shows where.
- `SourceLines` against `lines.physical`, and against `lines.physical` plus an estimate of the leading trivia lines Microsoft includes (comments, doc comments and directives directly above the declaration). The estimate is a text scan, not a lexer.

### Matching rule

Microsoft names members by display signature (`int C.M(int value)`, `int C.P.get`) with a `File` and a `Line` (the line of the member name). Skarp uses a syntactic id (`N.C::M(int)`) with a line span.

A Microsoft `Method` element (methods, constructors, operators, accessors) matches the Skarp top-level record (one without a `parent`) in the same file whose `startLine..endLine` contains the Microsoft line, with the same accessor suffix (`.get`, `.set`, `.init`, `.add`, `.remove`) or none. Among several candidates, the one whose simple name equals Microsoft's wins, then the smallest span. Each Skarp record matches at most once. The script counts simple-name mismatches as a sanity check.

Not matched by design:

- Microsoft `Property`, `Event` and `Field` elements (aggregates and field initializers; Skarp has no member record for them).
- Microsoft rows for members without a body (auto-accessors, abstract, interface, extern and partial-method definitions, positional record and primary constructors; Microsoft reports them with complexity 1).
- Members that exist on one side only because of `#if`. The script classifies unmatched members with a simple `#if` region scan, and reports matched members whose span holds directive or inactive lines in a separate "with #if" count, because the two tools parse with different symbols.

## 5. Recorded values (regression test data)

`tests/SkarpSharp.Tests/MicrosoftOracleTests.cs` checks `cyclomaticMicrosoft` of every edge-case member against Microsoft's value, recorded in `tests/SkarpSharp.Tests/Oracle/microsoft-cyclomatic-edgecases.json`. It runs in every `dotnet test`. One documented difference is allowed: `Branching.NonShortCircuitBool` (Microsoft 3, Skarp 1), because counting a non-short-circuit `&` or `|` needs operand types that a syntax tree does not have. The test also fails when an edge-case member has no recorded value.

After adding a construct to `tests/oracle/EdgeCases`, regenerate the data: do steps 1 and 2 for the edge cases, run Skarp on the same scratch copy (step 3), then:

```sh
node tools/oracle/compare-ms-metrics.mjs <scratch>/ec-ms.xml <scratch>/ec-ours.json --expected tests/SkarpSharp.Tests/Oracle/microsoft-cyclomatic-edgecases.json
```

Members whose Skarp value differs are written with `"knownDifference": { "skarp": <value>, "reason": "TODO" }`. Fix the code, or replace `TODO` with the reason. The recorded file came from Microsoft.CodeAnalysis.Metrics 5.6.0: 117 matched members, 116 equal.

## 6. Class coupling

`tests/oracle/Coupling` holds worked examples of class coupling (one namespace per example, 59 types). `tests/SkarpSharp.Tests/Oracle/microsoft-coupling-examples.json` records, per type, Microsoft's `ClassCoupling` and the expected Skarp values for `cbo` and `cboAll`. The test `SemanticMetricTests.Coupling_worked_examples_match_Microsoft_and_the_catalog` compiles the examples in memory and checks `cboMicrosoft`, `cbo` and `cboAll` of all 59 types.

To regenerate after adding an example:

```sh
# a scratch copy, built (Metrics.exe needs a built project) and given a solution for Skarp's semantic tier
cp -r tests/oracle/Coupling <scratch>/oracle-coupling
dotnet build <scratch>/oracle-coupling -c Release
<scratch>/metrics-5.6.0/Metrics.exe /project:<scratch>\oracle-coupling\Coupling.csproj /out:<scratch>\coupling-ms.xml
printf '<Solution>\n  <Project Path="Coupling.csproj" />\n</Solution>\n' > <scratch>/oracle-coupling/Coupling.slnx
dotnet run --project src/SkarpSharp.Cli -c Release -- analyze <scratch>/oracle-coupling --no-history --no-config -o <scratch>/coupling-ours.json
```

Then read `NamedType` `ClassCoupling` from `coupling-ms.xml` (namespace plus type name, `<T>` written as `` `1 ``, nested types joined with `+`) and `cbo`, `cboAll` and `cboMicrosoft` from `coupling-ours.semantic.json`, and write the JSON file in the format it already has.

## See also

- [CONTRIBUTING.md](../../CONTRIBUTING.md)
- [docs/rules.md](../../docs/rules.md)
- [docs/semantic-tier.md](../../docs/semantic-tier.md)
- [CREDITS.md](../../CREDITS.md)
