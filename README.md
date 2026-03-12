# ME_ACS SQL Patcher

Windows desktop tool for patching MagEtegra SQL Server backup files (`.bak`) from one supported version to another.

## What This Repo Contains

- WPF desktop application targeting `.NET 8`
- SQL patch library under `patches/`
- xUnit test project covering patching, settings, and storage behavior
- Packaging script for producing a portable support handoff ZIP

## Core Workflow

1. Select a source `.bak` file.
2. Choose the starting version.
3. Let the app resolve the latest reachable target version.
4. Run the patch workflow against a local SQL Server instance.

Default SQL target is `.\\MAGSQL`.

## Repository Structure

```text
.
|-- Assets/                  Application icons and images
|-- Infrastructure/         Runtime helpers and support utilities
|-- Models/                 Domain and configuration models
|-- Services/               Application services and patching logic
|-- ViewModels/             View-model layer
|-- Workflows/              End-to-end patch execution flow
|-- MagDbPatcher.Tests/     Automated test project
|-- patches/                Versioned SQL patch definitions
|-- tools/                  Support scripts such as patch-pack creation
|-- package.ps1             Portable packaging entry point
|-- ME_ACS_SQL_Patcher.sln  Solution file
```

## Development

### Requirements

- Windows
- .NET SDK 8.x
- Local SQL Server instance (Express, LocalDB, or full SQL Server)
- Permissions to restore, back up, create, and drop databases

### Build

```powershell
dotnet build ME_ACS_SQL_Patcher.sln
```

### Test

```powershell
dotnet test ME_ACS_SQL_Patcher.sln
```

## Packaging

Create the portable support package with:

```powershell
.\package.ps1
```

This produces:

- `output\ME_ACS_SQL_Patcher\` for local verification
- `dist\ME_ACS_SQL_Patcher.zip` for support handoff

Default packaging is self-contained single-file `win-x64`, so target machines do not need a separate .NET runtime installation.

## Support Handoff

After packaging:

1. Send `dist\ME_ACS_SQL_Patcher.zip` to support.
2. Support extracts the ZIP to any folder.
3. Support runs `ME_ACS_SQL_Patcher.exe` from the extracted root.
4. Future updates are delivered by replacing the extracted folder with the next packaged release.

Portable runtime layout:

```text
ME_ACS_SQL_Patcher.exe
patches\
settings.json
logs\
backups\
```

Runtime notes:

- `settings.json` and `logs\` live beside the EXE.
- `patches\` is the active patch library.
- `backups\` is used during manual patch-pack import.
- Temporary restore workspace is stored in `%ProgramData%\ME_ACS_SQL_Patcher\temp`.

## Patch Updates

The normal release flow is to ship a fresh ZIP that already includes the latest `patches/` content.

For admin-only patch updates, the application also supports importing a patch pack ZIP with this structure:

```text
MagPatchPack.zip
  patch-pack.json
  patches/
    versions.json
    patcher.config.json
    <version folders...>/*.sql
```

Create a patch pack with:

```powershell
.\tools\New-PatchPack.ps1 -PatchesFolder .\patches -PackVersion 20260203 -OutFile .\MagPatchPack.zip -Notes "7.2.3 build patches"
```

## CI

GitHub Actions is configured to restore, build, and test the solution on Windows for pushes to `main` and pull requests.

## Troubleshooting

| Issue | Suggested action |
| --- | --- |
| App does not open | Unblock the downloaded ZIP in file Properties, then extract and run again |
| Missing runtime error | Rebuild using `.\package.ps1` in self-contained mode |
| Startup says package is incomplete | Verify `patches\versions.json` and required `.sql` files are present |
| SQL Server not found | Install SQL Server Express or LocalDB |
| No upgrade path | Verify `versions.json` and patch definitions |
| Version list looks wrong | Check the configured active patches folder |

## Key Files

| File | Responsibility |
| --- | --- |
| `MainWindow.xaml` | Main user interface |
| `MainWindow.xaml.cs` | Main application interaction flow |
| `Infrastructure/AppRuntimePaths.cs` | Portable app layout for settings, logs, patches, temp data, and backups |
| `Services/PortableAppBootstrapService.cs` | Startup validation for package completeness and folder writability |
| `Services/PatchStorageService.cs` | Active patch-folder resolution |
| `Services/VersionService.cs` | Version graph and reachable upgrade path logic |
| `Services/PatchPackService.cs` | Patch-pack validation, backup, and atomic swap |
| `package.ps1` | Packaging and ZIP generation |
