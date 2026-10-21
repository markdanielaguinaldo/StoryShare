# Development and API

This page covers building, testing, release mechanics, the HTTP API, and the parts of the
implementation that are easy to break.

## Building

The plugin targets **Jellyfin 12.1.x**, which runs on .NET 10 and ships SkiaSharp 3.
Building requires the .NET 10 SDK.

```powershell
./build.ps1
```

The script publishes the plugin, stages only the plugin assembly plus its logo, writes
`meta.json` with a BOM-less UTF-8 encoder, creates
`artifacts/dist/storyshare_<version>.zip`, and updates `manifest.json` with the new
version, checksum, target ABI, and release URL.

Use `-NoManifest` for a local build that you do not intend to publish:

```powershell
./build.ps1 -NoManifest
```

The build metadata carries `targetAbi` and `framework`, so Jellyfin will not offer the
package to an incompatible server major.

## Release flow

1. Build the version you intend to publish, with a changelog:

   ```powershell
   ./build.ps1 -Version 1.5.0.0 -Changelog "What changed"
   ```

2. Create the GitHub release and upload the zip from `artifacts/dist/`.
3. Publish the updated `manifest.json` only after the release asset exists.

The order matters. If the manifest is published first, anyone refreshing the repository
URL in that window gets a 404 for the asset. Jellyfin verifies the manifest checksum
against the downloaded zip, so upload the exact zip the build produced. A published
version is effectively immutable because Jellyfin decides whether to offer an update
from the version number.

`meta.json` and `manifest.json` must be written without a UTF-8 BOM. Jellyfin reads the
files as text before deserializing them, and a leading BOM breaks parsing. `build.ps1`
uses an explicit BOM-less encoder for this reason.

## Dev harness

Card layout is fast to break and slow to check against a live server. The harness renders
every style to PNG and exercises share-token signing without Jellyfin:

```powershell
dotnet run --project tests/StoryShare.DevHarness
```

It prints the output directory and exits non-zero if an assertion fails. The renders
cover:

- every style, with and without artwork;
- the pale presets and a raw hex background;
- both directions of cover fitting;
- every animation loop, including Vinyl's spin and Float/Pulse on several styles;
- still images staying byte-identical whatever animation is selected;
- footer placeholder expansion and share-token compatibility.

The loop checks compare the seam between the last and first frame against an ordinary
frame step. A number catches a loop that does not close, but only looking at the PNGs
catches a card that moves badly. Float and Pulse also write their peak frame as
`animation-*.png` for that reason.

## Architecture

The renderer is split by responsibility:

```text
Api/StoryShareController.cs      HTTP surface; render options are query parameters
Services/StoryCardRenderer.cs    per-style layout, one Build* method each
Services/CardScene.cs            draws a prepared scene at any animation phase
Services/StoryLines.cs           text and chip line types, palette
Services/CardGeometry.cs         fixed 1080x1920 geometry and sampling options
Services/CardCache.cs            finished cards, keyed on everything that changes one
Configuration/CardStyles.cs      background presets, theme list, colour maths
```

A card is built once into a `CardScene`, then drawn repeatedly at a phase in `[0,1)`.
Static work — the blurred background, laid-out text, decorative geometry — is baked into
layers during the build. Only the background pan, the artwork's push-in or spin, and the
light sweep are recomputed per frame. Moving text or its drop shadows back into the
per-frame path is expensive and should be measured first.

Geometry is fixed at 1080×1920. Content stays inside a safe band because Instagram draws
its own interface over the top and bottom of a story.

### Loop construction

- The ordinary push-in uses `(1 - cos 2πp) / 2`, so the last frame lands where the first
  began.
- Float uses sines of a whole number of turns.
- Pulse beats twice per loop on `(1 - cos 4πp) / 2`.
- Vinyl rotates exactly once per loop. A rotation only meets itself at the seam after a
  whole number of turns, so the only way to change its speed is to change the loop
  length. It runs on `AnimationSpec.Spin`: 144 frames at 24 fps, played twice, for a
  12-second clip at 10 rpm.

The harness measures the seam rather than assuming it, and for Vinyl it asserts that the
seam step is no larger than an ordinary step — a whole step is the design.

### Video encoding

The encoder uses the ffmpeg path Jellyfin already has configured. It renders one loop
into a temporary MP4, then repeats that loop with `-stream_loop` and muxes in silent AAC
audio. Repeating in ffmpeg rather than redrawing keeps a 6-second clip at the cost of 2
seconds of drawing. `-stream_loop` cannot read from a pipe, which is why the first pass
writes a temporary file while frames are piped in as raw RGBA.

The output is 1080×1920, H.264 High, `yuv420p`, CRF 18, with `+faststart`. Silent audio
is deliberate: Instagram is unreliable with video that has no audio stream at all.

Timing per style:

| Style / animation | Loop | Output |
| --- | --- | --- |
| Default Auto | 60 frames at 30 fps | 6 s |
| Float / Pulse | 96 frames at 24 fps, played twice | 8 s |
| Vinyl | 144 frames at 24 fps, played twice | 12 s |
| Vibe Meter | 300 frames at 30 fps | 10 s |

### Web client integration

Jellyfin exposes no client-side plugin API, so the Story button reaches jellyfin-web
through `index.html`. The plugin does not edit that file. An `IStartupFilter` adds
middleware that injects a `<script>` tag into the response as it is served.

Serving it is deliberate. On a distro package install, the web root belongs to `root`
while Jellyfin runs as the `jellyfin` user, so writing to `index.html` fails with a
permission error. Response injection needs no write access and survives a server upgrade
that replaces the file.

Two consequences of the pipeline position:

- Response compression runs downstream, so `Accept-Encoding` is stripped from the request
  before the body is captured.
- Conditional headers are stripped too, otherwise a `304 Not Modified` arrives with no
  body to inject into.

`index.html` is therefore served with `Cache-Control: no-cache` and without an `ETag`.
Everything jellyfin-web loads afterwards is cached as before.

The injected script polls for the detail page's button row. Three signals must agree
before the button is added: a detail route, no active spinner, and a button row already
populated by jellyfin-web. Buttons carry the item id they were built for and are replaced
when it changes.

The Story button is a bare `<a is="emby-linkbutton" target="_blank">` with no JavaScript
on its click. That exact combination makes the Jellyfin Android app hand the URL to the
phone's browser instead of trapping it in a WebView with no download handler.
`window.open()`, `preventDefault()`, a click handler, or a `download` attribute each
break that delegation.

### Caching

`CardCache` holds completed renders only. The key covers the item id, its `DateModified`
and `DateLastSaved`, the per-card options, and a fingerprint of render-affecting settings,
including the brand logo path. The server's name is part of the key on its own account
because `{server}` expands into the footer.

Nothing listens for a configuration-changed event. Changing a setting simply makes old
entries unreachable; they age out. The cache is least-recently-used, bounded by the
configured MB budget, and has a two-hour backstop. A card larger than a quarter of the
budget is not stored. Sharing an in-flight render between callers was rejected because one
caller's cancellation could kill another's render.

## API

All routes listed below require a normal Jellyfin token except the public share route,
which is gated by an HMAC signature and an expiry. The web-client script is also served
anonymously because `index.html` loads it before authentication.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/StoryShare/Items/{itemId}/Card?theme=&comment=&background=&animation=&vibe=&vibeAnimate=&format=&download=` | Render a card directly for an authenticated caller. |
| `POST` | `/StoryShare/Items/{itemId}/ShareLink?theme=&comment=&background=&animation=&vibe=&vibeAnimate=&format=` | Mint a signed, expiring link. |
| `GET` | `/StoryShare/Styles` | Available styles, background presets, and animations. |
| `GET` | `/StoryShare/Items/{itemId}/Caption` | The item's tagline, if it has one. |
| `GET` | `/StoryShare/Items/{itemId}/Vibe` | Default Vibe Meter values. |
| `GET` | `/StoryShare/Public/{token}.{jpg\|png\|mp4}?download=` | Anonymous, signature-gated card. |

`format` accepts `jpg` (the default), `png`, or `mp4`. The share dialog uses the public
route rather than `Card`, because iOS will only offer **Save to Photos** on a plain URL.

Share tokens are signed with the key generated on first run and stored in the plugin
configuration. New fields are appended to the payload rather than inserted beside related
fields, so links minted by older versions still parse. `Styles` exists so the settings
page and the dialog do not keep separate copies of the style and background lists.

## Footer migration

Releases up to 1.1.0.0 shipped the literal default `Now playing in Project Mark` — one
particular server's name — so every other install printed a stranger's server on its
cards. 1.1.1.0 rewrites exactly that string to `Now playing in {server}` on first load and
leaves hand-edited text alone.

`ServerInfo` is a seam rather than an injected application host, because the dev harness
has no host. It is resolved per card, so renaming the server changes the next render
without a restart, and the name is in the cache key on its own account.

## Compatibility notes

Jellyfin 10.10 uses .NET 8 and SkiaSharp 2. Jellyfin 12.1 uses .NET 10 and SkiaSharp 3.
SkiaSharp 3 removed the `SKPaint` text API (`TextSize`, `MeasureText`, `DrawText`) in
favour of `SKFont`. Back-porting the current renderer therefore means rewriting the text
drawing, not only changing version numbers.

The dev harness targets `net10.0` and references the same Jellyfin and SkiaSharp versions
as the plugin.
