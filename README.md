# AudioAdjuster4Jellyfin

Adjust a movie's first embedded audio track from Jellyfin Web. Choose the percentage of the original volume, a fixed decibel reduction, or a target loudness. Adjustments run on the server one movie at a time and apply to future playback.

The plugin currently supports movies only. TV episodes and other media types are outside its current scope.

## Install from Jellyfin's plugin catalog

Requires Jellyfin Server 12.0 or later and a published AudioAdjuster4Jellyfin release.

1. Open **Dashboard → Plugins → Repositories** and add a repository named **AudioAdjuster4Jellyfin**.
2. Use the public repository's raw manifest URL. For `Awatatah/AudioAdjuster4Jellyfin` on `main`, the URL is `https://raw.githubusercontent.com/Awatatah/AudioAdjuster4Jellyfin/main/manifest.json`.
3. Open **Catalog**, select **AudioAdjuster4Jellyfin**, and install. Restart Jellyfin when prompted.

### Configure the plugin and movie button

1. Open **Dashboard → Plugins → Audio Adjuster**, choose your settings, and save.
2. For the **Adjust Audio** button on movie details pages, install [JavaScript Injector](https://github.com/n00bcodr/Jellyfin-JavaScript-Injector) and add an enabled script with a **Script URL** of `/AudioAdjuster/movie-button.js`. If Jellyfin uses a base URL such as `/jellyfin`, use `/jellyfin/AudioAdjuster/movie-button.js`. Reload Jellyfin Web.

You can also queue a movie from the Audio Adjuster dashboard by pasting its details URL or item ID. Use the dashboard's **Refresh jobs** button for status. The queue and recent job history are held in memory and reset when Jellyfin restarts.

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

Build with the .NET 10 SDK using `dotnet build -c Release`. To build against a different Jellyfin 12.x API version, pass a property such as `-p:JellyfinVersion=12.4.0`. For local development, copy the built DLL from `bin/Release/net10.0/` into a plugin subfolder and restart Jellyfin.

Set the project `Version`, then push a matching stable tag such as `v0.1.0`. GitHub Actions builds the ZIP and attaches it to a draft release. Publish the draft to trigger the catalog update. The workflow computes the checksum from the published ZIP and updates `manifest.json` on the default branch. If needed, rerun **Package release and update Jellyfin catalog** manually with the published tag. The workflow token needs write access to the default branch for the manifest commit.

## License

Copyright 2026 Trinity DevOps LLC. Licensed under [Apache-2.0](LICENSE). See [NOTICE](NOTICE).
