# Packaging and releasing

How Skarp is packed as a .NET tool, how versions are set, and how a release reaches NuGet and GitHub.

## Packages

Skarp ships as four NuGet packages. Users install one of them by name and the .NET SDK picks the right one.

| Package | Content | Built on |
|---|---|---|
| `Skarp` | The pointer package. It holds no program, only a list of the other three. | any OS |
| `Skarp.win-x64` | Native AOT `skarp.exe` for Windows x64 | Windows |
| `Skarp.linux-x64` | Native AOT `skarp` for Linux x64 | Linux |
| `Skarp.any` | Framework-dependent .NET 10 build (`skarp.dll` and its libraries); needs the .NET 10 runtime | any OS |

Every package has `LICENSE` and `THIRD-PARTY-NOTICES.md` at its root. The library project `SkarpSharp.Core` is not packable: it ships only inside the tool packages. No debug symbols are packed.

## How the RID-specific packages work

.NET 10 supports RID-specific tool packages. `src/SkarpSharp.Cli/SkarpSharp.Cli.csproj` sets:

```xml
<ToolPackageRuntimeIdentifiers>win-x64;linux-x64;any</ToolPackageRuntimeIdentifiers>
```

- `dotnet tool install` reads the pointer package, picks the package for the machine's runtime identifier (RID), and falls back to `Skarp.any` when the RID is not listed. Users run the same command on every platform.
- The pointer lists only RIDs that are built and published. If a listed package is missing at that version, the install fails; it does not fall back to `Skarp.any`. Add `osx-arm64`, `linux-arm64` and similar only together with a build for them.
- Native AOT does not cross-compile between operating systems. The Windows package must be built on Windows and the Linux package on Linux. That is why the release workflow builds on both.
- `PublishAot` alone does not change what `dotnet pack` makes: without the per-RID packs, a tool install would run the framework-dependent build.

## Packing locally

Run these from the repository root. Each command writes one package into `artifacts/packages`.

```sh
dotnet pack src/SkarpSharp.Cli -c Release -o artifacts/packages                              # pointer
dotnet pack src/SkarpSharp.Cli -c Release -r win-x64 -o artifacts/packages                   # on Windows
dotnet pack src/SkarpSharp.Cli -c Release -r linux-x64 -o artifacts/packages                 # on Linux
dotnet pack src/SkarpSharp.Cli -c Release -r any -p:PublishAot=false -o artifacts/packages   # framework-dependent
```

Notes:

- Pack from a clean publish folder. Tool packs take the publish folder as it is, so a folder left from an older build can carry files the current project drops.
- Native AOT needs a platform toolchain: the Visual Studio C++ tools on Windows, `clang` and `zlib1g-dev` on Linux. On Windows, run the RID pack from an "x64 Native Tools" prompt, or set `IlcUseEnvironmentalTools=true` as for `dotnet publish`.
- `-r any` can fail with NETSDK1032 when `Platform=x64` is set in the environment (as in a Visual Studio tools prompt). Run that pack from a normal prompt.

## Versioning

The version comes from the git tag through [MinVer](https://github.com/adamralph/minver), with the tag prefix `v`.

| Tag | Package version |
|---|---|
| `v0.1.0` | `0.1.0` |
| `v0.2.0-rc.1` | `0.2.0-rc.1` (a prerelease) |
| a commit after `v0.1.0`, no new tag | `0.1.1-alpha.0.<n>` |

Builds need the tags, so CI checks out the full history (`fetch-depth: 0`).

## Releasing

The workflow `.github/workflows/release.yml` runs when you push a tag that starts with `v`:

1. It runs the full CI workflow (`.github/workflows/ci.yml`) on Windows and Linux: restore, build, test, Native AOT publish, the check that the JIT and Native AOT builds give equal output, the packs, a local `dotnet tool install` of each package, and an archive of the Native AOT executable.
2. If CI passes, the `publish` job collects the packages from both platforms. The pointer and `Skarp.any` packages come from the Linux job, each RID package from the job of its own platform.
3. It logs in to nuget.org with trusted publishing (a short-lived key exchanged for the workflow's GitHub OIDC token; no API key is stored). It pushes `Skarp.win-x64`, `Skarp.linux-x64` and `Skarp.any` first and the pointer `Skarp` last, because every package the pointer lists must exist at the same version.
4. It creates a GitHub Release for the tag with generated release notes and the Native AOT archives (`skarp-<version>-win-x64.zip` and `skarp-<version>-linux-x64.tar.gz`, each with `LICENSE` and `THIRD-PARTY-NOTICES.md`). A tag with a hyphen, such as `v0.2.0-rc.1`, is marked as a prerelease.

To release:

```sh
git tag v0.1.0
git push origin v0.1.0
```

### One-time setup

The repository owner does this once. The same steps are in the comment at the top of `release.yml`.

1. On nuget.org, create a trusted publishing policy for this repository and the workflow file `release.yml`, for the package ids `Skarp`, `Skarp.win-x64`, `Skarp.linux-x64` and `Skarp.any`.
2. In GitHub, add the repository variable `NUGET_USER` with the nuget.org user name (Settings, Secrets and variables, Actions, Variables).

## Verifying an install

After a release, install the tool into a scratch folder and run it:

```sh
dotnet tool install --tool-path ./skarp-tool --version 0.1.0 Skarp
./skarp-tool/skarp --help
./skarp-tool/skarp analyze . --no-history --verbose
```

For a local build, add `--add-source artifacts/packages`. Installing the RID-specific (Native AOT) package on Windows writes a `skarp.cmd` shim into the tool path, which starts `.store/skarp/<version>/skarp.win-x64/<version>/tools/any/win-x64/skarp.exe`; installing the framework-dependent `Skarp.any` package writes a real `skarp.exe` apphost. A Claude Code hook in exec form cannot start the `.cmd` shim, so `skarp install claude` writes `cmd.exe /c skarp ...` entries on Windows when it finds the shim (see [Claude Code](agents/claude-code.md)); with `Skarp.any` or a `skarp.exe` on PATH it writes plain `skarp`.

The run header names the runtime: `(AOT)` for the Native AOT packages and `(JIT)` for `Skarp.any`. To check the fallback directly:

```sh
dotnet tool install --tool-path ./skarp-any --version 0.1.0 Skarp.any
./skarp-any/skarp analyze . --no-history --verbose
```

Both give the same findings for the same input. CI checks this on every change.

To look inside a package, rename `Skarp.<version>.nupkg` to `.zip` and open it: `LICENSE` and `THIRD-PARTY-NOTICES.md` are at the root.

## See also

- [installation.md](installation.md)
- [getting-started.md](getting-started.md)
- [CONTRIBUTING.md](../CONTRIBUTING.md)
- [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)
- [ARCHITECTURE.md](../ARCHITECTURE.md)
