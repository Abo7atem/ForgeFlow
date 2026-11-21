# ForgeFlow TikTok quality strategy

## Why the previous output did not look sharper

`TikTok60Ultra` is intentionally a preservation/upload experiment.  The
metadata and sample-table patches do not create new pixels, so VLC or the
ForgeFlow preview should look identical to the source.  Any difference can
only be observed after TikTok finishes its server-side transcode.

## New path: Web Signal

ForgeFlow now exposes **Web Signal (TikTok Web)**.  It adds a neutral `edts /
elst` entry to the video track and a small `u120` marker in `moov/udta`, while
preserving the normal movie and media timing.  Chunk offsets are repaired when
the metadata box is inserted before `mdat`, and the result is decode-tested.

This follows the least invasive method described by the open-source Upload120
project.  It is deliberately separate from the older **HD Metadata Patch
(legacy)**, which inflates sample tables and can confuse desktop players.

## Correct A/B test

1. Select **Web Signal (TikTok Web)** and the 50 or 70 Mbps profile.
2. Upload the generated `*_TikTok60Ultra.mp4` in TikTok Studio Web on desktop.
3. Do not use TikTok's crop, speed, effects, music, or in-upload editor.
4. Enable TikTok's high-quality toggle if it is shown, then publish.
5. Compare the downloaded TikTok result with the source and with a legacy file.

There is no public TikTok API flag that guarantees “no recompression” or a
specific output quality.  Therefore ForgeFlow labels this as an experiment,
does not claim a guaranteed visual increase, and keeps **Safe H.264** available.

`TikTok60Ultra` is capped at a 1080-class canvas (1920x1080 landscape or
1080x1920 portrait). Smaller sources are not upscaled by this delivery stage,
and Turbo Copy is disabled for sources above the cap so the output cannot
silently remain 4K.
