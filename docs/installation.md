# Installation

How to install, pin, check and remove Skarp, and how to build it from source.

## What you need

- To install with `dotnet tool`: the .NET 10 SDK (see https://dotnet.microsoft.com/download).
- To run a release archive on Windows or Linux x64: nothing else. The archive holds a Native AOT executable.
- To run the framework-dependent build (macOS and other platforms): the .NET 10 runtime.

Skarp reads git history by running your `git` command, so git should be on the `PATH` for history and hotspot features. Without git, `analyze` still works and prints a note that history was skipped.

## Install as a .NET tool

```
dotnet tool install -g Skarp
```

The NuGet package `Skarp` is a small pointer package. The .NET SDK picks the build for your machine:

| Your machine | Package the SDK installs | What runs |
|---|---|---|
| Windows x64 | `Skarp.win-x64` | Native AOT executable, no runtime needed |
| Linux x64 | `Skarp.linux-x64` | Native AOT executable, no runtime needed |
| Anything else, including macOS and ARM | `Skarp.any` | framework-dependent build, needs the .NET 10 runtime |

You type the same command in every case, and the command that starts it is `skarp`. You can also install the fallback explicitly with `dotnet tool install -g Skarp.any`. The framework-dependent build produces the same output as the Native AOT build.

Other installs:

```
# for one repository, through a tool manifest
dotnet new tool-manifest
dotnet tool install Skarp
dotnet tool run skarp analyze .

# into a folder of your choice
dotnet tool install --tool-path ./tools Skarp
```

A tool manifest (`.config/dotnet-tools.json`) pins the version for everyone who works on the repository. CI can then run `dotnet tool restore` and `dotnet tool run skarp ...`.

## GitHub Release archives

Each release at https://github.com/sirphilliptubell/skarp-sharp/releases has one archive per platform:

- `skarp-<version>-win-x64.zip`
- `skarp-<version>-linux-x64.tar.gz`

Each holds the `skarp` executable (`skarp.exe` on Windows), `LICENSE` and `THIRD-PARTY-NOTICES.md`. Unpack it somewhere on your `PATH`. There is no installer and nothing is written outside the folders you run Skarp on, except the settings and last-run files described in [settings.md](settings.md).

## Check the install

```
skarp --version
skarp --help
```

`skarp --version` prints the installed version (for example `skarp 0.1.0`); `skarp --help` prints the command list. If you see them, the tool runs. To check that analysis works end to end:

```
skarp explain
skarp analyze . --no-history --no-semantic
```

The first prints the rules and presets. The second analyzes the current folder. The run details (runtime, whether it is the AOT or JIT build, timing) are shown by `skarp analyze . --verbose`.

## Pin a version

```
dotnet tool install -g Skarp --version 0.1.0
dotnet tool update -g Skarp --version 0.1.1
```

Without `--version`, `dotnet tool install` takes the newest stable release. Add `--prerelease` to include prereleases such as `0.2.0-rc.1`. In CI, pin the version so that a new release cannot change the result of a gate. See [baselines-and-gate.md](baselines-and-gate.md). Thresholds and default rules may change between releases, which can change what a baseline considers known.

## Update and uninstall

```
dotnet tool update -g Skarp
dotnet tool uninstall -g Skarp
```

Skarp also leaves files in the repositories you ran it on. Delete them by hand if you want them gone:

- `.skarp/` (the saved last run, `.skarp/last-run/`, and per-file check data). Add it to `.gitignore`.
- `.skarp-baseline.json` (the baseline; you normally commit it).
- `.skarp.json` (repository settings; you normally commit it).
- `~/.skarp/` (personal settings and the tracking files of `skarp check`; `SKARP_HOME` replaces this folder).

## Build from source

You need the .NET SDK version listed in `global.json`.

```
git clone https://github.com/sirphilliptubell/skarp-sharp
cd skarp-sharp
dotnet build SkarpSharp.slnx -c Release
dotnet run --project src/SkarpSharp.Cli -c Release -- analyze .
```

To build the Native AOT executable for your own platform:

```
dotnet publish src/SkarpSharp.Cli -c Release -r win-x64 -o artifacts/aot
```

Use `-r linux-x64` on Linux. Native AOT does not cross-compile between operating systems. It needs a C++ toolchain: the Visual Studio C++ build tools on Windows, `clang` and `zlib1g-dev` on Debian and Ubuntu. How the packages are made is in [packaging.md](packaging.md). Contributor setup is in [../CONTRIBUTING.md](../CONTRIBUTING.md).

## Troubleshooting

- `skarp` is not found after `dotnet tool install -g`: the .NET tools folder (`~/.dotnet/tools`, `%USERPROFILE%\.dotnet\tools` on Windows) must be on your `PATH`. Open a new terminal.
- A message about a missing .NET runtime on macOS or ARM: the framework-dependent build needs the .NET 10 runtime, not just an older one.
- Semantic findings are missing: the semantic tier needs a `.sln` or `.slnx` file and a restored solution. See [semantic-tier.md](semantic-tier.md).

## See also

- [getting-started.md](getting-started.md)
- [packaging.md](packaging.md)
- [settings.md](settings.md)
- [../CONTRIBUTING.md](../CONTRIBUTING.md)
- [../THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)
