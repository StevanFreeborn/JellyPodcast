# Contributing to JellyPodcast

Thank you for contributing to JellyPodcast! Keep user-facing setup and feature documentation in [README.md](README.md) and API behavior documented in XML comments so this guide stays accurate over time.

## Prerequisites

- **.NET SDK**: Version pinned in [global.json](global.json)
- **Jellyfin Server** (optional, for manual integration testing): Target ABI defined in [Directory.Packages.props](Directory.Packages.props) and [build.yaml](build.yaml)

## Repository Layout

- `src/` — Jellyfin plugin source (`Jellyfin.Plugin.Podcast.csproj`, controllers, services, scheduled tasks, configuration UI)
- `tests/` — xUnit v3 unit test suite and coverage settings (`coverage.settings.xml`)
- `scripts/` — Local verification and release helpers (`test-coverage.cs`, `update-manifest.cs`)
- `.github/workflows/` — Continuous integration ([ci.yml](.github/workflows/ci.yml)) and release automation ([publish.yml](.github/workflows/publish.yml))

## Local Verification

Run the same checks enforced by CI:

```bash
# Restore NuGet packages and local .NET tools
dotnet restore
dotnet tool restore

# Verify formatting and analyzer code style
dotnet format Jellyfin.Plugin.Podcast.slnx --verify-no-changes --no-restore

# Build solution (zero warnings required)
dotnet build Jellyfin.Plugin.Podcast.slnx --configuration Release --no-restore

# Run unit tests (or generate Cobertura + HTML coverage report)
dotnet test --configuration Release --no-build --no-restore
dotnet run scripts/test-coverage.cs -- -c Release --no-build --no-restore

# Publish plugin + bundled dependencies to ./dist/
dotnet publish src/Jellyfin.Plugin.Podcast/Jellyfin.Plugin.Podcast.csproj -c Release -o ./dist
```

## Manual Testing in Jellyfin

1. Copy the published assemblies (`Jellyfin.Plugin.Podcast.dll` and `TagLibSharp.dll`) from `./dist/` into your Jellyfin server's plugin folder.
2. Restart Jellyfin and verify behavior in **Dashboard -> Plugins -> Podcast**.
3. Check Jellyfin logs for plugin output:

```bash
# Docker
docker logs jellyfin 2>&1 | grep -i podcast

# systemd
journalctl -u jellyfin -f | grep -i podcast
```

## Code Style & Conventions

Compiler and formatting rules are enforced automatically via [.editorconfig](.editorconfig), [tests/.editorconfig](tests/.editorconfig), and [Directory.Build.props](Directory.Build.props) (`AnalysisMode=All`, `EnforceCodeStyleInBuild=true`, `TreatWarningsAsErrors=true`, `CodeAnalysisTreatWarningsAsErrors=true`).

In addition, please follow these repository conventions:

- **XML documentation**: Document all public types and members cleanly (builds enforce `GenerateDocumentationFile`).
- **Multi-line parentheses (K&R style)**: Place the opening `(` at the end of the opening line, indent inner lines by 2 spaces, and place the closing `)` on its own line aligned with the opening statement.
- **Pattern matching**: Prefer `is` / `is not` / relational `is` patterns (`is null`, `is not null`, `is > 0`, etc.) and `is false` instead of `!` for boolean checks.
- **Time abstraction**: Inject `TimeProvider` (defaulting to `TimeProvider.System`) rather than calling static `DateTime` clock members (`DateTime.UtcNow` / `DateTime.Now`).

## Pull Requests & Releases

- Include or update unit tests in `tests/` for any functional change and verify all CI checks pass locally before opening a pull request targeting `main`.
- Update [README.md](README.md) when user-facing configuration, endpoints, or behavior change.
- For releases, `<Version>` in [Jellyfin.Plugin.Podcast.csproj](src/Jellyfin.Plugin.Podcast/Jellyfin.Plugin.Podcast.csproj) is the single source of truth; release packaging, checksums, and [manifest.json](manifest.json) updates are handled by [.github/workflows/publish.yml](.github/workflows/publish.yml) using metadata and release notes from [build.yaml](build.yaml).

## Resources

- [Jellyfin Plugin SDK](https://github.com/jellyfin/jellyfin-plugin-sdk)
- [Jellyfin Plugin Documentation](https://jellyfin.org/docs/general/server/plugins/)

## License

By contributing, you agree that your contributions will be licensed under the MIT License ([LICENSE](LICENSE)).