# Contributing to Skarp

How to report problems, propose changes and send pull requests to Skarp.

## What is welcome

Issues and small pull requests are welcome: bug fixes, documentation fixes, new tests, and small improvements.

The maintainer reviews every pull request. Features that do not fit the roadmap may be declined, even when the code is good. For anything larger than a small change, open an issue first and describe the problem you want solved. That avoids work on something that will not be merged.

Please read the [Code of Conduct](CODE_OF_CONDUCT.md). To report a vulnerability, follow [SECURITY.md](SECURITY.md) and do not open a public issue.

## Build and test

You need the .NET SDK version in `global.json` (10.0.404).

```
dotnet build SkarpSharp.slnx
dotnet test --solution SkarpSharp.slnx
```

Tests use TUnit on Microsoft.Testing.Platform. Many tests are Verify snapshot tests. After an intended change to an output, update the snapshots by running the tests once with `SKARP_UPDATE_GOLDEN=1` (golden outputs) or `SKARP_UPDATE_SHAPES=1` (shapes), then review the diff. The details are in `tests/SkarpSharp.Tests/Snapshots.cs`.

Native AOT publish is optional for most changes. It needs clang and zlib on Linux, and the Visual Studio C++ tools on Windows.

## Principles

- JIT equals AOT. The JIT build and the Native AOT build must produce the same output. CI compares them with `tools/ci/jit-aot-compare.sh`. Avoid reflection and other code that behaves differently under AOT.
- `TreatWarningsAsErrors` is on. A warning fails the build.
- Follow the style of the surrounding code.
- Behavior changes need tests. Bug fixes need a test that fails without the fix.

## Skarp checks its own code

The repository analyses itself. CI must be green, and Skarp's own gate must pass on your pull request. If you intentionally change what the gate sees, update the baseline:

```
skarp baseline .
```

This rewrites `.skarp-baseline.json`. Commit it with the change that caused it. See [baselines and the gate](docs/baselines-and-gate.md).

## Before you open a pull request

- The solution builds and all tests pass.
- Skarp's own gate passes.
- Documentation is updated where behavior changed.
- `CHANGELOG.md` has an entry under "Unreleased".

The maintainer also runs a private regression suite on real repositories before merging. A change can pass CI and still be held back by that suite. The maintainer will tell you what failed in general terms.

## License

Skarp is licensed under the [MIT license](LICENSE). By contributing you agree that your contribution is licensed under it. There is no contributor license agreement.

## See also

- [ARCHITECTURE.md](ARCHITECTURE.md)
- [Getting started](docs/getting-started.md)
- [Code of Conduct](CODE_OF_CONDUCT.md)
- [SECURITY.md](SECURITY.md)
