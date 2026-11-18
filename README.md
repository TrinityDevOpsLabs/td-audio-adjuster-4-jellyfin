# AudioAdjuster4Jellyfin

Adjust a movie's first embedded audio track from Jellyfin Web. Choose the percentage of the original volume, a fixed decibel reduction, or a target loudness. Adjustments run on the server one movie at a time and apply to future playback.

The plugin currently supports movies only. TV episodes and other media types are outside its current scope.

## Install from Jellyfin's plugin catalog

Requires Jellyfin Server 12.x and a published AudioAdjuster4Jellyfin release. The plugin catalog uses 12.0 as the minimum server version. The plugin checks the server's major version when it loads.

1. Open **Dashboard → Plugins → Repositories** and add a repository named **AudioAdjuster4Jellyfin**.
2. Use the raw manifest URL for `TrinityDevOpsLabs/td-audio-adjuster-4-jellyfin` on `main`: `https://raw.githubusercontent.com/TrinityDevOpsLabs/td-audio-adjuster-4-jellyfin/main/manifest.json`.
3. Open **Catalog**, select **AudioAdjuster4Jellyfin**, and install. Restart Jellyfin when prompted.

### Configure the plugin and movie button

Open **Dashboard → Plugins → AudioAdjuster4Jellyfin**, choose your sound settings, and save. You can adjust movies from this page without any extra plugin.

To show the **Adjust Audio** button on movie details pages, add the script manually after installing the Injector:

1. Install [JavaScript Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector) and restart Jellyfin.
2. Open **Dashboard → Plugins → JavaScript Injector** and add a script named **AudioAdjuster4Jellyfin**.
3. Set **Script URL** to `/AudioAdjuster/movie-button.js`. If you access Jellyfin under a base path such as `/jellyfin`, use `/jellyfin/AudioAdjuster/movie-button.js`.
4. Enable the script, save, and reload Jellyfin Web. Open a movie’s details page while signed in as an administrator to see **Adjust Audio**.

Under **Adjust a movie**, type at least three characters of a movie title and select from up to 10 matches across the libraries your account can access. Each match shows the release year and library name when available. You can also paste a movie details URL or item ID. Under **Adjustment progress**, use **Cancel** to remove a waiting movie or stop an active adjustment before it reaches the final file replacement. The queue and recent job history are held in memory and reset when Jellyfin restarts.

## Choose what changes

| Setting | Effect |
| --- | --- |
| Percentage | Keep 50–90% of the first track's original volume. Default is 80%. |
| Decibels | Reduce the first track by 1–6 dB. Default is −2 dB. |
| Normalize | Measure and adjust the track to −30 to −14 LUFS. Default is −23 LUFS. This can raise or lower the volume and requires two passes. |
| Replace the first audio track | Re-encode the first embedded audio track as AAC in the movie file. |
| Add a selectable adjusted track | Add an AAC track labeled **Adjusted Audio (AudioAdjuster4Jellyfin)** to the movie file. Pick it in Jellyfin's audio menu. The original audio tracks are kept. |
| Keep a backup | Save the complete original movie beside the source with an `.audioadjuster-original` suffix. Enabled by default and independent of the track option. |

**Normalize** raises the volume of tracks below the target and lowers the volume of tracks above it. For example, if a quiet track measures −30 LUFS and you leave the target at −23 LUFS, the plugin aims to make it 7 LU louder. A less negative LUFS value means louder audio. **Percentage** and **Decibels** only reduce volume.

The default is to replace the first audio track with a backup. Future adjustments use the existing backup when available. Turning off backups stops new backup creation. If you replace audio without a backup, another adjustment uses the current audio and can compound the change.

To restore a backup, wait for any job for that movie to finish, move the adjusted file out of the library, rename the backup to the original movie filename, then refresh the movie in Jellyfin.

## Media requirements

- A Jellyfin administrator must submit each job. The movie must be a local MKV or MP4 with embedded audio, in a folder writable by the Jellyfin server. Movies with multiple versions use the file associated with the submitted item ID.
- The server's configured FFmpeg and FFprobe are used. AudioAdjuster copies video and other mapped streams and writes a temporary remux in the movie folder before replacing the file. Available space must fit the remux and, if applicable, a full backup.
- Movie details integration is for Jellyfin Web. If the updated audio list does not appear immediately, refresh the movie's metadata.

## Build and release

Build with the .NET 10 SDK using `dotnet build -c Release`. Release builds target the Jellyfin 12.0 API so servers across the 12.x series can install them. CI uses the same API target. For local development, copy the built DLL from `bin/Release/net10.0/` into a plugin subfolder and restart Jellyfin.

Push the release commit, then create a GitHub release with a tag such as `v1.0.2`. Select the release commit as the tag target and publish. The tag sets the plugin build version, ZIP filename, and Jellyfin catalog version. Local builds use `0.0.0` by default. GitHub Actions builds and attaches the plugin ZIP if it is missing, then uses it to update `manifest.json` on the default branch. If the repository uses immutable releases, push the tag first so the workflow can attach the ZIP to a draft before you publish.

The workflow replaces the GitHub release description with notes generated from the tagged commits and uses those same notes in the Jellyfin catalog. To retry a published release, run **Package release and update Jellyfin catalog** manually with its tag.

The catalog job pushes `manifest.json` using the installed GitHub App. Set the repository variable `CATALOG_APP_CLIENT_ID` and the repository secret `CATALOG_APP_PRIVATE_KEY`. The app needs **Contents: Read and write** and an **Always allow** bypass on any branch ruleset that blocks its direct push to the default branch.

## License

Copyright 2026 Trinity DevOps LLC. Licensed under [Apache-2.0](LICENSE). See [NOTICE](NOTICE).
