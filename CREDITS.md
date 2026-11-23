# Credits

Skarp (the `skarp` command) is an independent implementation, licensed under the MIT License (see [LICENSE](LICENSE)). It does not contain code from the tools and works named below. It implements published definitions, formulas and threshold values that these people and projects made public, and this file records them, including the lineage (an original method and any later refinement we adopted are both listed).

Rules for this file:

- Only what the shipped code actually implements or uses as a default or a named preset is listed. Works we studied but did not use are not listed.
- Naming a source credits it as the origin of a published definition or number. It does not mean the author, vendor or project knows of, reviewed or endorses Skarp or the way we implemented their work. Where our counting differs from the original, the entry says so.
- Findings are "worth a look" suggestions, never verdicts on anyone's code.
- Product and company names are trademarks of their owners. They are used only to say where a published value comes from.
- Code of bundled libraries (Roslyn, CliWrap and others) is covered in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), not here.
- Citations come from public sources: the papers and documents themselves, vendor documentation, repositories and public bibliographic records. Anything we could not check is marked "unverified".

## 1. Metrics

Source: `src/SkarpSharp.Core/Metrics/`. Each metric is computed from Roslyn syntax trees with our own counting rules.

### Cyclomatic complexity
- **What we use:** the control-flow complexity count (decision points plus one), reported per member. Our default counts each `case` label, switch-expression arms, `catch`, `when`, `?:`, `&&`, `||`, `??`, `??=`, `?.` and `and`/`or` patterns. A `microsoft` preset reproduces the counting of Microsoft's metrics engine as far as syntax allows.
- **Original work:** Thomas J. McCabe, "A Complexity Measure", IEEE Transactions on Software Engineering SE-2(4), December 1976, pp. 308-320, doi 10.1109/TSE.1976.233837. (Checked against public bibliographic records only.)
- **Refinement adopted:** Arthur H. Watson and Thomas J. McCabe, "Structured Testing: A Testing Methodology Using the Cyclomatic Complexity Metric", NIST Special Publication 500-235, 1996 (counting of predicates, boolean operators and multiway decisions; the 10 / 15 limit discussion). https://nvlpubs.nist.gov/nistpubs/Legacy/SP/nistspecialpublication500-235.pdf
- **Microsoft preset:** counting rules of `Microsoft.CodeAnalysis.Metrics` (the metrics engine in the dotnet/roslyn repository) and the CA1502 rule ("Avoid excessive complexity", in the dotnet/sdk repository), Microsoft, both MIT. We read the behaviour and reimplemented it; the engine is used only as a test oracle (section 4).
- **Used in:** `Metrics/Cyclomatic.cs`; findings rule `complex-method` (cyclomatic option); the `microsoft` preset; WMC in rule `large-type`.

### Cognitive complexity
- **What we use:** the Cognitive Complexity measure: structural increments plus nesting level, flat increments for `else`, `goto` and runs of boolean operators, recursion counted once per member. Our decisions where the specification is silent (switch expressions, lambdas, `when`, null-conditional operators) are our own.
- **Original work:** G. Ann Campbell, "Cognitive Complexity - a new way of measuring understandability", white paper, version 1.7, SonarSource, 2023-08-29. https://www.sonarsource.com/docs/CognitiveComplexity.pdf
- **Refinement:** G. Ann Campbell, "Cognitive Complexity: An Overview and Evaluation", 2018 ACM/IEEE International Conference on Technical Debt (TechDebt 2018), doi 10.1145/3194164.3194186 (restates the counting rules).
- Credited as the published specification only. No SonarSource code or analysis output was read or used, and no endorsement by SonarSource is implied.
- **Used in:** `Metrics/Cognitive.cs`; rule `complex-method` (default metric); hotspot complexity axis (`History/HistoryJoin.cs` and the hotspot table).

### NPath
- **What we use:** the count of acyclic execution paths through a member, in a "paths" mode that follows the documented semantics of PMD 7.14 and later (early exits do not multiply), adapted to C#, with saturating 64-bit arithmetic.
- **Original work:** Brian A. Nejmeh, "NPATH: A Measure of Execution Path Complexity and Its Applications", Communications of the ACM 31(2), February 1988, pp. 188-200, doi 10.1145/42372.42379. The limit of 200 comes from this paper.
- **Refinement adopted:** the PMD project's NPath implementation (pmd/pmd, BSD-style license), behaviour read and reimplemented, no code copied. https://github.com/pmd/pmd
- **Used in:** `Metrics/NPath.cs`; rule `path-explosion`.

### Maximum nesting depth
- **What we use:** the deepest nesting of `if` (else-if flat), `switch`, loops and `try` in a member; the outermost opener is level 1.
- **Original idea:** Warren A. Harrison and Kenneth I. Magel, "A Complexity Measure Based on Nesting Level", ACM SIGPLAN Notices 16(3), 1981, pp. 63-74, doi 10.1145/947825.947829. Our counting rules are our own; the paper is credited for the measure's origin.
- **Used in:** `Metrics/NestingDepth.cs`; rules `deep-nesting` and `bumpy-road`.

### Line classification, statements and member counts
- **What we use:** physical, blank, comment-only, mixed, directive, inactive and source line kinds, classified from Roslyn tokens and trivia; statement count and member counts per member, type and file.
- **Framework:** Robert E. Park and the Size Subgroup of the Software Metrics Definition Working Group, "Software Size Measurement: A Framework for Counting Source Statements", CMU/SEI-92-TR-020 (ESC-TR-92-020), Software Engineering Institute, Carnegie Mellon University, September 1992. https://www.sei.cmu.edu/library/software-size-measurement-a-framework-for-counting-source-statements/ Credited for the practice of stating an explicit counting definition and for the precedence rule we follow (a line holding code and a comment counts as code). The line kinds themselves are the common vocabulary of line counters.
- **Line rule from scc:** blank lines inside a multi-line `/* */` comment count as comment lines, the choice documented by scc (Ben Boyter, https://github.com/boyter/scc). Behaviour only.
- **Used in:** `Metrics/LineClassifier.cs`, `Metrics/Statements.cs`.

## 2. History and hotspots

Source: `src/SkarpSharp.Core/History/`, `src/SkarpSharp.Core/Analysis/`.

### Change frequency and churn
- **What we use:** per file, over a window (12 months by default, anchored at the newest commit): the number of commits (change frequency, the primary activity value), lines added and lines deleted. Reported as activity, never as a defect probability.
- **Original work (change counts as an activity signal):** Todd L. Graves, Alan F. Karr, J. S. Marron, Harvey P. Siy, "Predicting Fault Incidence Using Software Change History", IEEE Transactions on Software Engineering 26(7), July 2000, pp. 653-661, doi 10.1109/32.859533.
- **Original work (churn as lines added and deleted):** Nachiappan Nagappan and Thomas Ball, "Use of Relative Code Churn Measures to Predict System Defect Density", 27th International Conference on Software Engineering (ICSE 2005), St. Louis. We implement the absolute measures only (added and deleted); the relative measures are not used.
- **Used in:** `History/FileAccumulators.cs`, `History/HistoryJoin.cs`.

### Activity hotspots
- **What we use:** files that change often and are also complex, shown as a two-axis table (activity first, then complexity), with the activity set taken from a window anchored at the latest commit, and the finding `active-complex-file` using a share of the most active files.
- **Original work:** Adam Tornhill, "Your Code as a Crime Scene: Use Forensic Techniques to Arrest Defects, Bottlenecks, and Bad Design in Your Programs", Pragmatic Bookshelf, 2015 (ISBN 978-1-68050-038-7), and "Software Design X-Rays: Fix Technical Debt with Behavioral Code Analysis", Pragmatic Bookshelf, 2018 (ISBN 978-1-68050-272-5); the hotspot analysis of CodeScene (CodeScene AB). (Book details checked against publisher and bookseller records; only public excerpts were read, not the full books.)
- **Share values:** the 2 to 4 percent (CodeScene) and 1 to 3 percent (Tornhill) figures informing our 4 percent default are vendor claims; see the preset notes in section 3.
- **Used in:** `Analysis/`, `History/HistoryJoin.cs`, rule `active-complex-file`.

### Git history reading, rename handling and author identity
- **What we use:** the output of the user's own `git` command line (`git log --numstat` with rename detection at 50 percent similarity, `.mailmap` applied) through a process call. No git code is included; git is a project of the Git community (GPL-2.0), run as a separate program.
- Our preprocessing choices (merge commits skipped, pure moves counted as no change, bot detection by name and email patterns, flagging commits over 300 C# files) are our own design choices.

## 3. Findings and threshold presets

Source: `src/SkarpSharp.Core/Findings/Presets.cs`, `RuleCatalog.cs`, `Rules.cs`. Thresholds are facts published by the sources below; each finding shows the preset and source that supplied its value. Presets are named after their sources: `default` (composite), `codescene`, `clean-cadet`, `doomen`, `reportgenerator`, `ndepend`, `pmd`, `checkstyle`, `microsoft`, `jscpd`, `alves-percentile`. No code of these tools is used. Finding ids, the baseline file and its matching (`Baselines/`) are our own design and use no other tool's algorithm or format.

The "over threshold" counts in the summary (`Analysis/Aggregation.cs`: cyclomatic 10, cognitive 15, NPath 200, nesting 3, statements 30) are our own recommendations drawn from the sources credited in sections 1 and 3 (Watson and McCabe, the cognitive defaults, Nejmeh, the nesting and statement limits of CodeScene, NDepend, Checkstyle and PMD).

### CodeScene (preset `codescene`; used in `default`)
- **What we use:** C# default thresholds from the vendor's published default rules file (dated 2025-05-09): cyclomatic 9 (alert 100), nesting depth 4, function lines 70 (alert 500), file lines 1000 (alert 5000), 4 function arguments and 5 constructor arguments, complex conditional 2, bumpy road 2 bumps at depth 2. The Bumpy Road idea is CodeScene's; our detection is our own. Counting rules and units are not published by the vendor, so our counting is used.
- **Origin:** CodeScene AB, "What are the default threshold values for different programming languages?", CodeScene Help Center, and the linked default-rules file (https://codescene.com/hubfs/Technical%20Support%20Data/default-rules.zip); CodeScene blog post on the Bumpy Road code smell (Adam Tornhill), https://codescene.com/blog/bumpy-road-code-complexity-in-context/. The 2 to 4 percent hotspot figure comes from CodeScene's whitepaper "Behavioral Code Analysis in Practice" (https://codescene.com/hubfs/web_docs/Behavioral-code-analysis-in-practice.pdf).
- **Rules:** `complex-method`, `deep-nesting`, `long-method`, `large-file`, `too-many-parameters`, `bumpy-road`, `complex-condition`, `active-complex-file`.

### Clean-CaDET, University of Novi Sad (preset `clean-cadet`; used in `default`)
- **What we use:** Long Method as more than 50 physical lines (default) and the combined rule with cyclomatic over 10; Large Class as more than 100 lines or WMC over 20 (default for `large-type`). The published results of these rules on human labels are the evidence behind the defaults.
- **Origin:** the Clean-CaDET project (Luburić, Prokić, Slivka, Kovačević, Sladić, Vidaković et al.), evaluation notebooks `Clean-CaDET/ML-code-smell-CSharp` (MIT), and the labelled data (section 4). Papers: N. Luburić et al., "Towards a systematic approach to manual annotation of code smells", Science of Computer Programming, 2023; "Prescriptive procedure for manual code smell annotation", Science of Computer Programming, 2024, doi 10.1016/j.scico.2024.103168.
- **Rule lineage as cited in the Clean-CaDET notebooks:** the 50-line Long Method rule from Amin Milani Fard and Ali Mesbah, "JSNose: Detecting JavaScript Code Smells", IEEE 13th International Working Conference on Source Code Analysis and Manipulation (SCAM 2013), pp. 116-125; the rule "lines over 50 or cyclomatic over 10" and the Large Class rule (class lines over 100 or WMC over 20) from Hui Liu, Zhiyi Ma, Weizhong Shao and Zhendong Niu, "Schedule of Bad Smell Detection and Resolution: A New Way to Save Effort", IEEE Transactions on Software Engineering 38(1), 2012, pp. 220-235. (Citations as given by Clean-CaDET; the originals were not read by us, and first names other than the surnames given there are from general knowledge. Unverified.)
- **Rules:** `long-method`, `complex-method` (cyclomatic option), `large-type`.

### Chidamber and Kemerer (WMC)
- **What we use:** Weighted Methods per Class, taken as the sum of member cyclomatic complexity, in the `large-type` rule (WMC over 20).
- **Original work:** Shyam R. Chidamber and Chris F. Kemerer, "A Metrics Suite for Object Oriented Design", IEEE Transactions on Software Engineering 20(6), June 1994, pp. 476-493, doi 10.1109/32.295895. (Checked against public bibliographic records only.)

### Microsoft (preset `microsoft`)
- Microsoft code analysis rule CA1502 "Avoid excessive complexity": cyclomatic complexity 25, applied to Microsoft's counting; and rule CA1506 "Avoid excessive class coupling", 95 types, applied to our reimplementation of Microsoft's coupling count (`Semantic/MicrosoftCoupling.cs`). Source: Microsoft documentation and the analyzer source in the dotnet/sdk repository (MIT).

### ReportGenerator (preset `reportgenerator`; used in `default`)
- Risk hotspot thresholds of ReportGenerator (cyclomatic 15, NPath 200) by Daniel Palme, https://github.com/danielpalme/ReportGenerator (Apache-2.0). Values only.

### NDepend (preset `ndepend`; used in `default`)
- Default rule and metric values of NDepend (NDepend SAS): cyclomatic 15 and 30, IL nesting depth 4 and 8, logical lines 20 and 40, types with more than 20 methods or 15 fields, more than 5 parameters (metric documentation) and 8 or more parameters (rule), and TypeCe (efferent coupling of a type) 50 with third-party types counted. NDepend counts on compiled IL; our syntax counts approximate it, which the preset description says.

### PMD (preset `pmd`)
- Default properties of the PMD design rules (PMD project, Java): CyclomaticComplexity 10, CognitiveComplexity 15, NPathComplexity 200, NcssCount 60 (method) and 1500 (class), TooManyMethods 10, TooManyFields 15, ExcessiveParameterList 10, AvoidDeeplyNestedIfStmts 3, CouplingBetweenObjects 20 (class coupling). https://pmd.github.io

### Checkstyle (preset `checkstyle`; also magic-number and empty-catch design)
- Default properties of Checkstyle checks (Checkstyle project, Java): CyclomaticComplexity 10, NestedIfDepth 1, MethodLength 150, MethodCount 100, JavaNCSS class maximum 1500, FileLength 2000, ParameterNumber 7, NPathComplexity 200, BooleanExpressionComplexity 3, and the ClassFanOutComplexity 20 (class coupling) and the `MagicNumber` ignore list (-1, 0, 1, 2) used by rule `magic-number` and the `EmptyCatchBlock` check that our `empty-catch` design follows. Values only; our two-level severity design for empty catch blocks is our own. https://checkstyle.org

### Doomen C# coding guidelines (preset `doomen`)
- Dennis Doomen, "C# Coding Guidelines" (AV rules): AV1500 (methods over 15 statements), AV1561 (more than 3 parameters), AV1515 (no magic numbers; the positions where literals are allowed inform our C# exemptions, including unit conversions), AV1210 (do not swallow errors by catching generic exceptions; informs the empty-catch design). https://github.com/dennisdoomen/CSharpGuidelines and https://www.csharpcodingguidelines.com
- **License:** the guidelines are licensed Creative Commons Attribution-ShareAlike 4.0 International (CC BY-SA 4.0). Skarp does not reproduce their text: it uses the rule numbers and numeric limits only, and its descriptions are our own words. If any guideline text is ever reproduced (for example in `skarp explain`), it needs attribution and the ShareAlike terms of https://creativecommons.org/licenses/by-sa/4.0/.

### Designite (complex-condition default)
- The default of three boolean sub-expressions (two operators in our count) is taken from the default thresholds of DesigniteJava by Tushar Sharma, https://github.com/tushartushar/DesigniteJava (Apache-2.0). Values only. Reference: Tushar Sharma, Pratibha Mishra and Rohit Tiwari, "Designite: A Software Design Quality Assessment Tool", 1st International Workshop on Bringing Architectural Design Thinking into Developers' Daily Activities (BRIDGE 2016, at ICSE), ACM, pp. 1-4, doi 10.1145/2896935.2896938.

### Published defaults of other tools used in the `default` preset
- **SonarSource rule S3776 (cognitive complexity 15), detekt `CognitiveComplexMethod` (15, inactive by default) and Lizard's cognitive-complexity extension (15):** the default cognitive threshold of 15 is named as supported by these tools. The Sonar value is secondary-source evidence only (SonarSource staff statements quoted in search results; the public rule page does not show the parameter default to our fetcher; unverified from a primary Sonar page). PMD's 15 (above) is the primary-source backing. Lizard (Terry Yin) and detekt are credited for their published defaults only.

### Brain Method and duplication thresholds (CodeScene)
- **Brain Method:** the rule `brain-method` (a member that has the long-method, complex-method and deep-nesting findings together) follows the Brain Method code smell as CodeScene describes it (size, complexity, nesting); the grouping and its parameters are our own. Lanza and Marinescu's original Brain Method also has a fourth leg (many accessed variables) that Skarp leaves out. Reference: Michele Lanza and Radu Marinescu, *Object-Oriented Metrics in Practice*, Springer, 2006 (read secondhand; unverified against the book).
- **Near duplicates:** the defaults of `duplicate-code-near` (similarity 0.75, at least 10 lines) are chosen after CodeScene's function-level duplication check as described in its public documentation; the similarity measure (multiset Jaccard over subtree fingerprints) is our own. The all-pairs join with a prefix filter follows Roberto J. Bayardo, Yiming Ma and Ramakrishnan Srikant, "Scaling Up All Pairs Similarity Search", WWW 2007 (citation from general knowledge; unverified).

### Clone detection (rules `duplicate-code` and `duplicate-code-near`)
- **What we use:** token-based detection of exact copies and copies with consistently renamed identifiers, using Brenda S. Baker's parameterized matching (identifiers encoded by the distance to their previous occurrence) and a rolling hash over token windows (the idea of Karp and Rabin, 1987). The Type 1, 2 and 3 clone vocabulary is the common one of the clone-detection literature. All code is our own. Citations from general knowledge, unverified: B. S. Baker, "On Finding Duplication and Near-Duplication in Large Software Systems", WCRE 1995; B. S. Baker, "Parameterized Duplication in Strings: Algorithms and an Application to Software Maintenance", SIAM Journal on Computing 26(5), 1997.
- **Default size:** 100 tokens over at least 10 lines, from the SonarQube duplication rule for languages other than Java and the PMD CPD documentation example (public documentation, values only). Preset `jscpd`: 50 tokens and 5 lines, the defaults of jscpd (Andrey Kucherenko and contributors, MIT, https://github.com/kucherenko/jscpd; values only). The low-content filter and the warning limits (50 lines, 3 copies of 20 lines) are our own.

### Class cohesion and the God Class rule (rule `god-class`)
- **Cohesion:** tight class cohesion (TCC) and loose class cohesion (LCC) as defined by James M. Bieman and Byung-Kyoo Kang, "Cohesion and Reuse in an Object-Oriented System", ACM SIGSOFT Symposium on Software Reusability (SSR 1995) (citation from general knowledge; unverified). Our treatment of calls between methods is described in `Semantic/Cohesion.cs`.
- **Detection strategy:** Marinescu's God Class strategy (WMC very high, ATFD above few, TCC below one third) from Lanza and Marinescu 2006 (see above), with the Java values taken from secondary sources: the PMD `GodClassRule` defaults (preset `lanza-marinescu-2006`, WMC 47, ATFD 5), the iPlasma variant (WMC 20, ATFD 4) and an ATFD 2 variant also used by Clean-CaDET. The book was not read by us; the values are unverified against it. They are Java statistics, not a C# calibration.
- **Used in:** `Semantic/Cohesion.cs`, `Semantic/GodClassRule.cs`, `Findings/GodClassPresets.cs`; rule `brain-class` (its `lanza-marinescu-2006` preset uses the book's size legs, secondhand).

### Class coupling (rule `high-coupling`)
- Coupling between object classes (CBO) as introduced by Chidamber and Kemerer 1994 (see above), counted by our own semantic walk; the limit of 20 from Checkstyle and PMD (above), 50 in the NDepend preset, 95 in the `microsoft` preset (CA1506).

### Untested complex methods (rule `untested-complex-method`)
- The cut of cyclomatic complexity 6 is derived from the CRAP index of Alberto Savoia and Bob Evans (2007, Crap4j): CRAP = cyclomatic^2 x (1 - coverage)^3 + cyclomatic, over 30 at zero coverage from complexity 6. Skarp does not compute CRAP. Coverage is read from Cobertura XML reports (Coverlet, Microsoft code coverage and ReportGenerator output); no code of those tools is used. Details from general knowledge; unverified.

### Unused code (rules `unused-member`, `unused-type`, `unread-member`)
- The detection follows the meaning of Roslyn's IDE0051 (unused private member) and IDE0052 (unread private member), widened to internal members and types, with our own reachability rules. Microsoft documentation only; no analyzer code is used. The "used only by tests" label is after NDepend's rule of that kind.

### Code health score
- The 1 to 10 scale and the bands (9 or more healthy, 4 to below 9 problematic, below 4 unhealthy) follow the scale of the Code Health measure used in Adam Tornhill and Markus Borg, "Code Red: The Business Impact of Code Quality", TechDebt 2022 (citation from general knowledge; unverified), with the labels CodeScene publishes. The score formula, the rule weights and the decay are our own and are not CodeScene's algorithm. Bands are for display only.

### Benchmark percentiles (preset `alves-percentile`)
- **Method:** Tiago L. Alves, Christiaan Ypma, Joost Visser, "Deriving Metric Thresholds from Benchmark Data", IEEE International Conference on Software Maintenance (ICSM 2010), Timișoara, pp. 1-10. The benchmark-based percentile risk bands. (Checked against public bibliographic records only.)
- **Values (cyclomatic, methods per type, file lines):** Kazuhiro Yamashita, Changyun Huang, Meiyappan Nagappan, Yasutaka Kamei, Audris Mockus, Ahmed E. Hassan, Naoyasu Ubayashi, "Thresholds for Size and Complexity Metrics: A Case Study from the Perspective of Defect Density", IEEE International Conference on Software Quality, Reliability and Security (QRS 2016), pp. 191-201, doi 10.1109/QRS.2016.31. The 80th and 90th percentile ranges for open-source Java systems (Table IV) are their replication of the Alves method.
- **Values (method lines):** Shaiful Alam Chowdhury, Gias Uddin, Reid Holmes, "An Empirical Study on Maintainable Method Size in Java", 19th International Conference on Mining Software Repositories (MSR 2022), doi 10.1145/3524842.3527975, arXiv 2205.01842 (method size percentiles 36 and 63).

### Namespace dependency cycles (rule `namespace-cycle`)
- **What we use:** strongly connected components of the namespace dependency graph as the unit of one finding, the shortest cycle by breadth-first search, mutual pairs with their lighter direction, and a greedy weighted feedback-edge suggestion (our own heuristic, labelled a suggestion).
- **Algorithm:** Robert E. Tarjan, "Depth-First Search and Linear Graph Algorithms", SIAM Journal on Computing 1(2), 1972, pp. 146-160, doi 10.1137/0201010 (strongly connected components; implemented iteratively in our own code).
- **Principles:** John Lakos, *Large-Scale C++ Software Design*, Addison-Wesley, 1996 (levelization: a physical design without cyclic dependencies); Robert C. Martin, the Acyclic Dependencies Principle (*Agile Software Development, Principles, Patterns, and Practices*, Prentice Hall, 2002). Both read secondhand (unverified against the books).
- **Vendors:** NDepend (NDepend SAS) rules "Avoid namespaces mutually dependent" and "Avoid namespaces dependency cycles" (the lighter direction of a mutual pair as the one to cut, namespaces kept per assembly); Structure101 (tangles, a feedback set, default tangle threshold 0 percent). Public documentation only; no product was run.
- **Used in:** `Semantic/CycleGraph.cs`, `Semantic/NamespaceCycles.cs`.

## 4. Calibration and validation data

These are used to measure and test the tool. The data and packages named here are not shipped in the Skarp packages and are not part of this repository (the tests that read their results are).

### Clean-CaDET labelled C# smell dataset (calibration of long-method and large-type)
- **What we use:** the Long Method (2,574 labelled methods) and Large Class (920 labelled classes) labels, to measure precision and recall of our rules. Labels are used for measurement only; thresholds were not tuned to them.
- **Dataset:** Nikola Luburić, Simona Prokić, Katarina-Glorija Grujić, Jelena Slivka, Aleksandar Kovačević, Goran Sladić, Dragan Vidaković (University of Novi Sad, Faculty of Technical Sciences), "Towards a systematic approach to manual annotation of code smells - C# Dataset of Long Method and Large Class code smells", version 1.0, Zenodo, 2022-05-05, doi 10.5281/zenodo.6520056, https://zenodo.org/records/6520056
- **License:** Creative Commons Attribution 4.0 International (CC BY 4.0), https://creativecommons.org/licenses/by/4.0/.
- **Changes made:** we converted the label spreadsheets to JSON, normalized the rows and matched each label to a member or type record of our tool, to compute measurements. The data and the derived files are not included in this repository and are not redistributed. Any redistribution of the data or of derived tables must carry the attribution above (creators, title, link, license link, changes).
- **Evaluation notebooks:** `Clean-CaDET/ML-code-smell-CSharp` (MIT, Clean-CaDET); we compare against their published rule results, no code reused.
- The labelled source snippets come from open-source projects (BurningKnight, Core2D, jellyfin, OpenRA, ShareX, ShopifySharp, MonoGame, osu) at pinned commits; they are not included in this repository and keep their own licenses.

### Newtonsoft.Json (test corpus)
- James Newton-King and contributors, Newtonsoft.Json, https://github.com/JamesNK/Newtonsoft.Json (MIT), pinned at commit `52fa3aef1f2cadcd3a3f874251eddc98d3efbbaa`. Used as a real-code corpus in a separate test suite outside this repository: comparison against Microsoft's metrics engine, history preprocessing counts and finding identity stability. Not copied into this repository and not shipped.

### Microsoft.CodeAnalysis.Metrics (test oracle)
- Microsoft, `Microsoft.CodeAnalysis.Metrics` 5.6.0 (MIT), run offline to compare cyclomatic complexity and line counts with our `microsoft` preset (`tests/SkarpSharp.Tests/MicrosoftOracleTests.cs`, [tools/oracle/README.md](tools/oracle/README.md)). Not shipped.
