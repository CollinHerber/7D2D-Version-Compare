# Contributors

This file covers local development, build verification, and release automation for 7D2D Version Compare.

## Tech Stack

- .NET 10
- Avalonia 12
- DiffPlex for line-based XML diffs
- xUnit for core comparison tests

## Repository Layout

```text
src/
  VersionCompareTool/
  VersionCompareTool.Core/
tests/
  VersionCompareTool.Core.Tests/
eng/
  Get-ReleasePlan.ps1
.github/
  workflows/
    release.yml
```

`Versions/` and `Mods/` are local data folders and are intentionally gitignored. Do not commit game files or mod files into the repository.

## Local Development

Install the .NET SDK version listed in `global.json`.

```powershell
dotnet restore VersionCompareTool.slnx
dotnet build VersionCompareTool.slnx
dotnet test VersionCompareTool.slnx
dotnet run --project src/VersionCompareTool/VersionCompareTool.csproj
```

During source development, the app resolves `Versions` and `Mods` from the repository root. For packaged builds, those folders sit beside `VersionCompareTool.exe`.

## Test Data

Create local test folders like this:

```text
Versions/
  2.6/
    Data/
      Config/
        items.xml
  3.0/
    Data/
      Config/
        items.xml
Mods/
  ExampleMod/
    Config/
      items.xml
```

The app compares only `*.xml` files. Paths are normalized to `/`, compared case-insensitively, and mod conflict matching ignores a leading `Data/` segment.

## Diff Cache

The default diff cache lives under:

```text
%LOCALAPPDATA%/7D2D-Version-Compare/DiffCache
```

The cache key includes the selected start/end versions, resolved folder paths, and a metadata fingerprint for each version folder. The fingerprint tracks XML relative paths, file counts, total bytes, latest write time, and a metadata hash over path, size, and last-write timestamps.

The cache stores only the base version diff. Mod conflict overlays are applied after the base diff is loaded, which keeps mod selection independent from the version diff cache.

## Windows Publish

```powershell
dotnet publish src/VersionCompareTool/VersionCompareTool.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o artifacts/publish/win-x64
```

For packaged builds, place `Versions` and `Mods` beside `VersionCompareTool.exe`. The release workflow adds placeholder folders for both.

## Release Automation

GitHub Actions publishes Windows releases from `.github/workflows/release.yml` on pushes to `main` or manual `workflow_dispatch` runs.

The workflow first computes the release plan. If no release is needed, it exits before .NET setup, restore, test, publish, tag, or release steps run.

When a release is needed, the workflow:

- Restores and tests the solution on `windows-latest`.
- Computes the next version from conventional commits since the latest `vMAJOR.MINOR.PATCH` tag.
- Publishes a self-contained `win-x64` build.
- Creates a Git tag and GitHub Release.
- Uploads `7D2D-Version-Compare-v<version>-win-x64.zip`.

If no version tag exists yet, the first release is `v0.1.0`.

Supported changelog commit types:

```text
feat: add version diff navigation
fix: prevent mod compare from freezing
chore: update release workflow
```

Version bump rules:

- Breaking `feat` or `fix` commits, such as `feat!: ...` or a `BREAKING CHANGE:` footer, bump major.
- `feat` commits bump minor.
- `fix` commits bump patch.
- `chore` commits are included in release notes when a release is already being created, but do not bump the version.
- If there are no `feat` or `fix` commits since the latest tag, the release is skipped before .NET setup, restore, test, publish, tag, or release steps run.

Release notes are grouped into Features, Fixes, and Chores.
