# Installation

Story Share is a Jellyfin plugin. It renders cards from items already in your library
and does not run a separate service.

## Requirements

- **Jellyfin 12.1.x.** Current releases target ABI `12.1.0.0` and the .NET 10 runtime
  used by Jellyfin 12.1.
- **The standard Jellyfin web client.** The share dialog and the item-page button are
  part of jellyfin-web.
- **ffmpeg for video cards.** Jellyfin normally ships and configures ffmpeg. Still cards
  do not need it.

If you install a release manually, you also need write access to Jellyfin's plugin
directory. Installing from the catalog handles that for you.

## Install from the plugin repository

This is the recommended method and is how future updates are delivered.

1. In Jellyfin, open **Dashboard → Plugins → Repositories**.
2. Select **+** and add:

   ```text
   https://raw.githubusercontent.com/markdanielaguinaldo/StoryShare/main/manifest.json
   ```

3. Open **Catalog**, find **Story Share** under **General**, and install it.
4. Restart Jellyfin.
5. Hard-refresh the web client (<kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>R</kbd>, or
   <kbd>Cmd</kbd>+<kbd>Shift</kbd>+<kbd>R</kbd> on a Mac) so the browser loads the
   plugin's script.

Jellyfin verifies the package checksum from the repository manifest before installing.

## Manual installation

Download `storyshare_<version>.zip` from
[Releases](https://github.com/markdanielaguinaldo/StoryShare/releases), then extract it
into a folder for the plugin:

| Platform | Plugin path |
| --- | --- |
| Linux / Docker | `/config/plugins/StoryShare/` |
| Windows | `%ProgramData%\Jellyfin\Server\plugins\StoryShare\` |

Restart Jellyfin afterwards, then open **Dashboard → Plugins → Story Share**.

> If Jellyfin runs with custom paths (`--datadir`), the plugin folder lives under that
> data directory, not `%ProgramData%` or `/config`. Check the server's start-up command
> if the plugin does not appear after a restart.

Manual installations do not update themselves. Repeat the extraction with the new zip
when a new release is published.

## After installation

1. Open **Dashboard → Plugins → Story Share** and review the defaults.
2. Set **Public base URL** if share links need to work outside your local network.
3. Reload the web client and open any movie, show, episode, album, or track.
4. Select the share icon in the detail button row.

If the button does not appear, see [Troubleshooting](troubleshooting.md#the-share-button-does-not-appear).

## Upgrading

Catalog installs appear under **Dashboard → Plugins** when a newer compatible version is
available. Install the update and restart Jellyfin, then hard-refresh the web client.

Plugin settings live in Jellyfin's plugin configuration and survive normal upgrades. Do
not delete the configuration file to force an update: it contains the share-link signing
key, and removing it invalidates every link that has already been shared.

## Compatibility

Current releases target Jellyfin 12.1.x on .NET 10. The repository manifest also lists
older releases for previous server versions. Jellyfin only offers a package whose target
ABI matches your server.

Jellyfin 10.10 uses .NET 8 and SkiaSharp 2. Jellyfin 12.1 uses .NET 10 and SkiaSharp 3.
SkiaSharp 3 replaced the `SKPaint` text API with `SKFont`, so back-porting the current
renderer is a code change, not just a version-number change. See
[Development](development.md#compatibility-notes) for the technical details.
