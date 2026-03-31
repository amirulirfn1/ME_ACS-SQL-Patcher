# Design: Update System, Version Alignment & Admin Enhancements

**Date:** 2026-03-31
**Status:** Approved

---

## Overview

Three focused changes:
1. Align app version to MagEtegra version numbering (start at 7.2.4)
2. Set a default update URL and add a silent startup update check with inline notification
3. Add three small enhancements to the Admin Tools window

---

## 1. Version Alignment

**Change:** `Version`, `FileVersion`, `AssemblyVersion`, and `InformationalVersion` in `src/ME_ACS_SQL_Patcher/ME_ACS_SQL_Patcher.csproj` → `7.2.4`.

**Rationale:** The app version should match the MagEtegra database version it targets. When a new patch (e.g. 7.2.5) is released, the app version bumps to match.

`AppMetadata.DisplayVersion` reads from assembly attributes automatically — title bar, Admin window header, and build label will all update with no other code changes.

---

## 2. Default Update URL

**Change:** In `src/ME_ACS_SQL_Patcher/Models/AppSettings.cs`, set `AppUpdateFeedPath` default value to:

```
http://magetegra.servecounterstrike.com
```

**Rationale:** Fresh installs or missing settings files currently leave the feed path blank, requiring manual entry in Admin. With a hardcoded default, update checks work out of the box for all users.

**Infrastructure note (out of scope for this implementation):**
`magetegra.servecounterstrike.com` is a No-IP dynamic DNS hostname pointing to the developer's PC. For it to serve updates, the following must be configured on that PC:
- No-IP DUC client running (auto-updates DNS when public IP changes)
- Router port forwarding: external port 80 → PC's local IP, port 80
- Static local IP for the PC (DHCP reservation in router)
- Static file server (e.g. Caddy) serving the `update-host/` folder on port 80

---

## 3. Startup Update Check

**Where:** `MainWindow_Loaded`, after `_settings` is loaded and before `_viewModel.StatusText = "Ready"`.

**Behaviour:**
- Fire an `async` background task (do not `await` on the main startup path — do not block or slow startup)
- Use the existing `VelopackAppUpdateService.CheckForUpdatesAsync` with the resolved feed path
- If result is `UpdateReadyToApply`: show the existing inline banner with message:
  `"Version X.Y.Z is available. Go to Admin → App Update to apply it."`
- If result is anything else (no update, not installed, error): do nothing, show nothing
- Errors are swallowed silently — update check must never crash or interrupt the startup flow

**Why on startup only:** Avoids background polling overhead. Startup is the natural moment users expect a check. Can be expanded to a periodic check later.

---

## 4. Admin Tools Enhancements

Three additions to `AdminWindow`. All are simple, no new services needed.

### 4a. "Open Patches Folder" Button

- Placed in the **Settings** tab near `txtActivePatchesFolder`
- On click: `Process.Start("explorer.exe", folder)` where folder is `txtActivePatchesFolder.Text`
- Disabled if the folder text is empty or the folder does not exist

### 4b. "Test Update Server" Button

- Placed in the **App Update** tab near `txtUpdateFeedPath`
- On click: fires an `HttpClient.GetAsync` with a 5-second timeout to the feed URL
- Shows result in the existing `bdAppUpdateStatus` banner:
  - Success (2xx): `"Update server is reachable."` (Success style)
  - Failure / timeout: `"Cannot reach update server: <reason>"` (Error style)
- Does not use Velopack — just a plain HTTP ping so it works even from uninstalled dev builds

### 4c. Last Checked Timestamp Label

- Small `TextBlock` below the update feed path, e.g. `"Last checked: 31 Mar 2026, 9:41 AM"` or `"Never checked"`
- Stored in `AppSettings` as `LastUpdateCheckAt` (`DateTime?`, nullable)
- Written to settings after each update check (both from startup check and from Admin manual check)
- Displayed in Admin window on load; refreshed after each check

---

## 5. Future Improvements (not in this implementation)

After these changes ship, the three recommended next improvements are:

1. **Patch release notes** — serve a `changelog.txt` alongside the Velopack feed; when an update is found, fetch and display it in the banner
2. **Recent SQL servers dropdown** — persist the last 5 SQL server names in `AppSettings`, show as a dropdown on the main window (same pattern as recent backup files)
3. **SQL connection status indicator** — on startup, if `LastSqlServer` is saved, silently ping it and show a green/red dot next to the server field

---

## Files Affected

| File | Change |
|---|---|
| `src/ME_ACS_SQL_Patcher/ME_ACS_SQL_Patcher.csproj` | Version → `7.2.4` |
| `src/ME_ACS_SQL_Patcher/Models/AppSettings.cs` | Default `AppUpdateFeedPath`, add `LastUpdateCheckAt` |
| `src/ME_ACS_SQL_Patcher/Views/MainWindow.xaml.cs` | Startup update check (background task) |
| `src/ME_ACS_SQL_Patcher/Views/MainWindow.Events.cs` | Banner helper for update notification (if not already present) |
| `src/ME_ACS_SQL_Patcher/Views/AdminWindow.xaml` | New button + label in Settings and App Update tabs |
| `src/ME_ACS_SQL_Patcher/Views/AdminWindow.xaml.cs` | Load/display `LastUpdateCheckAt` |
| `src/ME_ACS_SQL_Patcher/Views/AdminWindow.Actions.cs` | Open folder action, test server action, save timestamp |
