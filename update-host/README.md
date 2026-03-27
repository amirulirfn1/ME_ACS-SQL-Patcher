LAN-based Update Feed
=====================

This folder lets your PC act as a simple LAN update host for the installed app. It is meant for internal testing or a small office setup, not as the long-term production update server.

How it works
------------

1. Run `build\release.ps1` so the latest Velopack installer artifacts land in `dist\installer`.
2. Run `update-host\Publish-UpdateFeed.ps1`. It copies the release feed files into `update-host\feed`.
3. Share `update-host\feed` from your PC over the network.
4. In each installed app, open **Advanced Settings**, set **App Update Feed** to the UNC share path, and click **Check Updates**.

Recommended flow
----------------

1. Build a release:
   `.\build\release.ps1`
2. Publish the feed:
   `.\update-host\Publish-UpdateFeed.ps1`
3. Review the suggested share command:
   `.\update-host\Share-UpdateFeed.ps1`
4. Share the folder and give teammates a UNC path like:
   `\\YOUR-PC-NAME\ME_ACS_SQL_Patcher_Updates`

Scripts
-------

- `Publish-UpdateFeed.ps1`
  Copies everything from `dist\installer` into `update-host\feed`.
- `Share-UpdateFeed.ps1`
  Shows the recommended `net share` command and reports whether the share already exists.

Notes
-----

- The app must be installed with the setup build for Velopack update checks to work.
- This folder is safe to keep in Git because the actual published feed contents are ignored except for `.gitkeep`.
- If your PC is off, teammates cannot check for updates from this feed.
- If you later move to cloud hosting, you can reuse the same release artifacts and point the app at a new feed URL instead.
