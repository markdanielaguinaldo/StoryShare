# User Guide

## Making a card

1. Open a movie, show, episode, album, or track in jellyfin-web.
2. Select the share icon in the detail button row.
3. Choose a **Style** and a **Background**.
4. Choose **Still image** or **Video (for Stories)** and, for video, an **Animation**.
5. Add an optional caption, or use the item's tagline suggestion.
6. Select **Go to URL** to open the signed link on your phone.

The preview updates when the style, background, format, animation, caption, or Vibe
Meter values change. There is no separate refresh control.

## Styles

| Style | Description |
| --- | --- |
| **Poster** | The cover on a blurred version of its own artwork. |
| **Full bleed** | The cover printed edge to edge, with the title, facts, and footer set along the bottom left. |
| **Vibe Meter** | The cover above five playful, metadata-derived meters. |
| **Polaroid** | The cover in a square print on a tilted paper card, with the caption below. |
| **Vinyl** | The cover cut into a record, with grooves, a label ring, and a spindle hole. Spins in video. |
| **Stack** | The cover fanned out as a pile of cards, with the front one face up. |
| **Ticket** | A cinema stub with the cover printed across its head and a perforated tear-off. |
| **Crate** | The cover at the front of a crate, with two sleeves receding behind it. |
| **Review** | Blurred artwork, a glowing cover, large type, a star score, and the caption as the review. |

The accent colour used for chip outlines, rules, and other details comes from each item's
artwork unless an accent is pinned in the plugin settings.

## Backgrounds

The Story dialog offers **Match the artwork**, sixteen presets, and a custom colour.

- **Vibe Meter, Vinyl, Stack, Crate, and Review** paint the background directly.
- **Polaroid and Ticket** paint the card stock itself, lifting the colour towards white,
  and darken the surrounding area so the card still stands off it. **Match the artwork**
  keeps the classic white stock.
- **Poster and Full bleed** keep the item's artwork behind the card and tint the blurred
  backdrop towards the chosen colour. The cover itself stays true.

The **Paper** and **Bone** presets flip the card to dark type. The same switch happens
for a custom colour light enough to need it. Poster and Full bleed stay in white type on
their own dark scrim; Polaroid and Ticket decide from the lightness of the stock.

### Artwork fitting

Some styles put the cover into a window with a different shape: Polaroid's print is
square, Vinyl's label is a circle. Cropping a 2:3 poster to fill one of those can leave a
middle strip with neither the title nor any faces.

When the cover and its window are more than about a quarter apart in aspect ratio, the
cover is set whole inside the window on a blurred, darkened copy of itself. Closer
matches are cropped to fill as usual. The fitted cover has a small margin so the
animation's push-in grows into it instead of clipping it.

Vinyl is excluded because a fitted rectangle turning behind a round hole would not read
as a record. Full bleed and Ticket always crop to fill: for them the picture is the
surface, and a cover on a blurred bed is the look they exist to avoid.

## Animation and video

For most styles, choosing **Video (for Stories)** reveals an **Animation** control:

| Animation | What moves |
| --- | --- |
| **Auto** | A slow push-in on the cover, a blurred backdrop drifting the other way, and a light sweep across the artwork. Vinyl spins. |
| **Float** | The whole card drifts through a figure of eight while the background and text stay put. |
| **Pulse** | The artwork swells and accent light breathes behind the card, twice per loop. |

Vinyl always keeps its record spin. Vibe Meter has its own meter animation instead of
the Auto/Float/Pulse list: turn **Animate meter** on for a video, or off for a still.

Video output is deliberately conservative so Instagram Stories accepts it:

| Property | Value |
| --- | --- |
| Size | 1080×1920 |
| Container / codec | MP4, H.264 High, `yuv420p` |
| Duration | 6 seconds for Auto; 8 seconds for Float and Pulse; 12 seconds for Vinyl; 10 seconds for Vibe Meter |
| Audio | Silent AAC stereo at 44.1 kHz |
| Fast start | Enabled |

Motion is delivered as video, not GIF. Instagram flattens a GIF added to a story into a
still image.

## Captions and taglines

The **Caption on the card** box is free text and starts empty. When an item has a
tagline, the dialog offers **Use the tagline: "…"**. Selecting it only types the tagline
into the caption box, where it can be edited or removed. A tagline is never applied
automatically; many are marketing copy that does not belong on a story. Items without a
tagline show no suggestion.

Review treats the caption as the body of the review, without quotation marks and with
room for four lines.

## Vibe Meter

Vibe Meter draws five scores: **Excitement**, **Tears**, **Snacks Required**, **Plot
Twists**, and **Couch-Lock**. The initial values come from the item's genres, runtime,
rating, and a stable per-item variation.

Each score can be adjusted from 0 to 100 in the dialog. **Animate meter** produces a
10-second clip where the bars fill over the first three seconds and then hold. Turn it
off to get a still image with the bars already filled.

## Saving to a phone

**Go to URL** opens a short-lived signed link to the rendered file.

### Android

The link opens in the device's default browser, where the file downloads normally. In
the Jellyfin Android app, the button is handed to that browser rather than opening a
WebView with no download handler.

### iPhone and iPad

Safari may display an image instead of downloading it. Press and hold the preview, then
choose **Save to Photos**. For a video, open the link and use the share button, then
choose **Save Video**.

iOS ignores both the `download` attribute and `Content-Disposition: attachment`, which
is why the preview is a plain signed URL rather than a `blob:` URL.

## The share button

The button is an icon-only share control in the item page's button row. It is added to
jellyfin-web by a script the plugin serves with `index.html`; no file on disk is changed.
It appears only when **Add a share button to item pages** is enabled.

The button waits until the detail page has finished loading and carries the item id it
was built for, so it cannot point at the previous item during navigation.

If it does not appear, see [Troubleshooting](troubleshooting.md#the-share-button-does-not-appear).
