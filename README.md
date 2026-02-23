# ME_ACS SQL Patcher

A tool to patch MagEtegra database backups (`.bak`) from one version to another.

---

## Quick Start

1. Run `ME_ACS_SQL_Patcher.exe`.
2. Select your source `.bak` file.
3. Choose **From** version (the app auto-selects **To = latest reachable**).
4. Click **Start Patch**.

Default SQL target on fresh installs is `.\\MAGSQL`.

---

## Build Distribution ZIP

```powershell
.\package.ps1
```

Output:
- App payload: `publish/`
- Shareable archive: `dist/ME_ACS_SQL_Patcher.zip`

Default packaging is self-contained single-file (`win-x64`) so target PCs do not need a separate .NET runtime install.

---

## Install On Another PC (Portable, No Admin)

After extracting the ZIP, run:

```powershell
.\scripts\Install-Portable.ps1 -SourceDir .\publish
```

What it does:
- Installs app files to `%LOCALAPPDATA%\MagDbPatcher\app`
- Creates desktop and start-menu shortcuts
- Preserves user settings and imported patches in `%LOCALAPPDATA%\MagDbPatcher`

Optional switches:
- `-NoDesktopShortcut`
- `-NoStartMenuShortcut`
- `-NoLaunch`

---

## Patch Updates (Without Rebuilding App)

Operators can import new script packs directly in the app using **Import Patch Pack (.zip)**.

Patch packs use this structure:

```text
MagPatchPack.zip
  patch-pack.json
  patches/
    versions.json
    patcher.config.json
    <version folders...>/*.sql
```

Create a patch pack:

```powershell
.\tools\New-PatchPack.ps1 -PatchesFolder .\patches -PackVersion 20260203 -OutFile .\MagPatchPack.zip -Notes "7.2.3 build patches"
```

Import result:
- Existing active patch folder is backed up as `*_backup_YYYYMMDD_HHMMSS`.
- New pack becomes active immediately after validation.

---

## Patch Storage Behavior

- Fresh users default to writable patches path: `%LOCALAPPDATA%\MagDbPatcher\patches`
- On first run, bundled app patches are copied there if the folder is empty
- Existing users with a configured patches folder are not overridden

---

## Requirements

- Windows
- Local SQL Server instance (Express/LocalDB/full). Remote servers are not supported.
- SQL permissions to restore, backup, create, and drop databases

---

## Troubleshooting

| Issue | Solution |
|------|------|
| App does not open | Unblock downloaded ZIP in file Properties, then extract and run again |
| Missing runtime error | Rebuild with `.\package.ps1` default mode (self-contained) |
| SQL server not found | Install SQL Server Express or LocalDB |
| No upgrade path | Check `versions.json` and patch definitions |
| Version list looks wrong | Verify the active **Patches Folder** in app |

---

## Key Files

| File | Purpose |
|------|------|
| `MainWindow.xaml` | Main UI |
| `MainWindow.xaml.cs` | Main workflow logic |
| `Services/PatchStorageService.cs` | Writable patch folder default + first-run seed logic |
| `Services/VersionService.cs` | Version/patch definitions and upgrade path logic |
| `Services/PatchPackService.cs` | Patch pack import + validation + atomic swap |
| `package.ps1` | Build + package ZIP |
| `scripts/Install-Portable.ps1` | Per-user portable installation helper |
