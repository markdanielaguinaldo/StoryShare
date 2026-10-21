# Configuration

Plugin settings live under **Dashboard → Plugins → Story Share**. They set the defaults
for new cards; the Story dialog can still change the style, background, animation, and
caption for an individual card.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| **Default style** | Poster | Preselected style in the Story dialog. |
| **Default animation** | Auto | Preselected animation for video. A still is always the card at rest. |
| **Background** | Match the artwork | Default background for new cards. |
| **Custom background colour** | Empty | Hex colour used when **Background** is set to **Custom colour**. |
| **Footer text** | `Now playing in {server}` | Small line at the bottom of every card. Empty hides it. |
| **Brand logo (Full bleed)** | Empty | Full path to an image on the server, printed in the top-left corner of Full bleed cards. |
| **Accent colour** | Empty | Hex accent for chip outlines, rules, and other details. Empty derives it from each item's artwork. |
| **Show the year** | On | Includes the production year in the subtitle or facts. |
| **Show genres** | On | Includes genres in the subtitle or facts. |
| **Show the community rating** | On | Includes the rating where the style shows one. |
| **Show the runtime** | On | Includes the runtime where the style shows one. |
| **Add a share button to item pages** | On | Injects the Story button into jellyfin-web item pages. |
| **Public base URL** | Empty | Externally reachable URL, such as `https://jellyfin.example.com`. Required for share links to work off your network. |
| **Share link lifetime (minutes)** | 60 | How long an unsigned-in share link stays valid. Accepted range: 5 to 10080 minutes. |
| **Keep finished cards in memory** | On | Caches completed stills and videos so repeat requests do not redraw. |
| **Cache size (MB)** | 64 | Memory budget for the cache. Accepted range: 8 to 512 MB. |

### Footer text

`{server}` expands to the name in **Dashboard → General**. The name is read for each
card, so renaming the server updates future renders without a restart. If the server has
no name set, the placeholder falls back to `Jellyfin`.

Leaving the field empty hides the footer. The history behind this setting — including the
old hardcoded default — is covered in [Development](development.md#footer-migration).

### Brand logo

The logo is a path, not an upload, because a plugin has nowhere durable to store an
uploaded file. Use a full path that the Jellyfin process can read.

Full bleed prints the logo flush against the top-left corner. Use an image with
transparency around the mark; an opaque rectangle reads as a block stuck to the card.
This corner sits outside the vertical safe band, so Instagram's own interface can cover
part of it while a story is being viewed. An unreadable path is logged and ignored rather
than failing the render.

### Backgrounds

The dialog offers **Match the artwork**, sixteen presets, and a custom colour. Preset ids
and raw hex colours are interchangeable in share links; both `?background=midnight` and
`?background=%232E1A47` work.

What the background affects depends on the style. See
[Backgrounds](user-guide.md#backgrounds) in the user guide.

## Share links

The dialog renders a preview, then mints a signed URL for **Go to URL**. The file is
served anonymously from `/StoryShare/Public/{token}` so a phone without a Jellyfin
session can open it.

- Tokens are HMAC-SHA256 signed and carry an expiry.
- The default lifetime is 60 minutes; the accepted range is 5 minutes to 7 days.
- **Public base URL** is used when set. Otherwise the link uses the address the browser
  reached the server on, which normally only works on the local network.
- Changing the signing key invalidates every outstanding link. The key is generated on
  first run and stored in the plugin configuration.

A share URL is a bearer credential for that one card until it expires. Use HTTPS on the
public URL, and avoid posting links somewhere you would not post the card itself.

## Caching

The dialog tends to request the same card twice: once for the preview and once when the
opened link renders. The cache keeps completed renders in memory to avoid paying for the
second one, which matters most for animated Vinyl cards.

The cache key includes the item id, the item's modification timestamps, the per-card
options, and a fingerprint of the render-affecting settings, including the brand logo
path. Changing a setting such as the footer or accent simply makes older entries
unreachable; there is no restart needed.

The cache is least-recently-used, bounded by **Cache size (MB)**, and also expires
entries after two hours. A single card larger than a quarter of the budget is not stored.
Only completed renders are cached: two identical requests arriving together both draw.
Turn the cache off with **Keep finished cards in memory**.
