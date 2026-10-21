# Troubleshooting

Start with the Jellyfin log. Story Share writes render failures, ffmpeg errors, and web
client hook status there. The exact log location depends on how Jellyfin is installed.

## The share button does not appear

1. Confirm **Add a share button to item pages** is enabled in
   **Dashboard → Plugins → Story Share**, then restart Jellyfin.
2. Look for this line in the server log at startup:

   ```text
   StoryShare: web client hook installed
   ```

   If it is missing, the plugin did not load. Check the plugin version and the server's
   plugin directory.
3. Hard-refresh the web client. The old `index.html` may still be cached from before the
   plugin was installed or upgraded.
4. Open an item detail page, not a library list. The button is only added to detail
   pages that have finished loading.
5. If you use a reverse proxy or a custom base path, confirm the web client and
   `/StoryShare/*` are served through the same host and path.

The button is part of jellyfin-web. Clients that do not use the web client will not show
it.

## The button appears on the wrong item, or on a loading screen

That was a bug in older releases. The current button requires three signals to agree — a
detail route, no active loading spinner, and a button row already populated by
jellyfin-web — and it carries the item id it was built for. Upgrade to the current
release and hard-refresh.

## Share links do not open outside my network

Set **Public base URL** to the address phones can reach, for example:

```text
https://jellyfin.example.com
```

Without it, the link uses the address the browser reached the server on, which is
normally a LAN address. Also check:

- The URL includes the correct scheme (`https://`, not `http://`, when TLS is used).
- Your reverse proxy forwards `/StoryShare/*` to Jellyfin.
- The port is reachable from the phone's network.
- The link has not expired.

## A share link stopped working

- **Expired:** the default lifetime is 60 minutes. Change **Share link lifetime** if you
  need longer.
- **Signing key changed or the plugin configuration was deleted:** every existing link is
  invalidated. Mint a new one.
- **The item was removed:** the link points at a library item id, so deleting the item
  makes the card unavailable.

## Video rendering fails

Video cards need ffmpeg. Confirm Jellyfin has a valid ffmpeg path in its own settings and
that the server can execute it.

The log reports errors such as:

```text
No ffmpeg is configured on this server, so animated cards cannot be produced.
ffmpeg timed out while building the animated card.
```

If a video keeps failing, render a still to confirm the card layout itself works. An
animated Vinyl card takes longer than other styles because the record must turn exactly
once per loop.

## Rendering fails with a 500 error

The response says to check the server log. The log entry includes the item name and the
exception.

If only one item fails, check its artwork and metadata. If every item fails, confirm the
plugin version matches Jellyfin 12.1.x and that the server can write to its temporary
directory.

## iPhone or iPad will not save the card

Safari displays images instead of downloading them. Press and hold the preview, then
choose **Save to Photos**. For a video, open the link and use the share button, then
choose **Save Video**.

This is expected behaviour: iOS ignores both the `download` attribute and the
`Content-Disposition: attachment` header. Story Share uses a plain signed URL so the
long-press save option is available.

## The plugin is not listed after a manual install

- Confirm the files are inside the plugin directory, not in a nested folder created by
  extracting a zip that already contains one.
- Check the server's actual `--datadir`; a custom data directory moves the plugin folder.
- Confirm the package targets Jellyfin 12.1.x. Jellyfin will not offer an incompatible
  package in the catalog, and a manually installed mismatch can stop the plugin loading.
- Restart Jellyfin and check the log for a load error.

## The cache is using memory

Turn off **Keep finished cards in memory**, or lower **Cache size (MB)**. Cached entries
also expire after two hours, and a single card larger than a quarter of the budget is not
stored. Changing render settings makes older entries unreachable, so the cache may take a
moment to settle after a settings change.

## Known limitations

- Story Share creates a file for you to post. It does not publish to Instagram or any
  other social network.
- Animated output is MP4. A GIF would be flattened to a still by Instagram Stories.
- Cards are fixed at 1080×1920. Other aspect ratios are not supported.
- The share button depends on jellyfin-web. Native clients that do not use the web client
  do not get the button.
- The plugin renders one item at a time from the item detail page; there is no batch
  export.
