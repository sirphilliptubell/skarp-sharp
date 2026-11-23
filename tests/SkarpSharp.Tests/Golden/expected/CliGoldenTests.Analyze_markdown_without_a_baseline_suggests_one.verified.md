## skarp analyze: 2 files

12 findings worth a look (2 warning, 10 notice) at 6 places in 2 files. Findings are places worth a look, not violations.

### Code health

average 7.4 over 2 files; hotspot health needs git history (syntax scores, 1 to 10, 10 = healthy; informs only).

| Score | File | Main contributions |
|---:|---|---|
| 6.9 | `src/OrderBook.cs` | empty-catch 1 warning -0.8, long-method 2 notices -0.7, complex-condition 2 notices -0.5 |

No baseline file: findings are not marked new or worsened, and `--format github` annotates warnings only (the first 10). Suggestion: run `skarp baseline` and commit `.skarp-baseline.json`, so CI shows only new and worsened findings.

### Top places (4 of 6 in 2 of 2 files)

| File | Place | Findings |
|---|---|---|
| `Program.cs`<br>health 9.5, 1 finding at 1 place, worst 1.3 x limit | `Program.cs:3` `<top-level>` | notice deep-nesting: nesting depth 4 (limit 3) [984d5d0e] |
| `src/OrderBook.cs`<br>health 6.9, 8 findings at 5 places, worst 1.3 x limit, +2 more places | `src/OrderBook.cs:45` `OrderBook.Describe` | notice deep-nesting: nesting depth 4 (limit 3) [b4a90bd3]<br>notice long-method: physical lines 25 (limit 20) [9b6c4c34] |
|  | `src/OrderBook.cs:10` `OrderBook.Total` | notice long-method: physical lines 21 (limit 20) [6e523882]<br>notice complex-condition: boolean operators 2 (limit at least 2) [0ed1ff77] |
|  | `src/OrderBook.cs:34` `OrderBook.Add` | warning too-many-parameters: parameters 8 (limit at least 8) [1a9e9b41]<br>warning empty-catch: empty bare catch without a comment [4a5cab44] |

<details><summary>Thresholds used</summary>

| Rule | Level | Limit | Source |
|---|---|---|---|
| deep-nesting | notice | nesting depth over 3 | default: CodeScene: depth 4 |
| long-method | notice | physical lines over 20 | custom: custom (threshold override) |
| too-many-parameters | warning | parameters at least 8 | default: NDepend: 8 or more |
| complex-condition | notice | boolean operators at least 2 | default: CodeScene: 2; Designite: 3 boolean sub-expressions |
| empty-catch | warning | structural | default: Skarp's own, after Checkstyle EmptyCatchBlock and Doomen AV1210 |

</details>

skarp {toolVersion}; `skarp explain <rule>` explains a rule.

### Semantic tier

not evaluated: no .sln or .slnx file found (the semantic tier needs a solution; pass one with --solution <file> or use --no-semantic)
