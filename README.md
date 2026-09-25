# Shoal Subtitles

Part of **Shoal**, a family of Jellyfin plugins that work together: [Shoal
Ingest](https://github.com/chrisgrulau/jellyfin-ingest) files new media into your libraries, [Shoal
Subtitles](https://github.com/chrisgrulau/jellyfin-subtitles) finds, checks and synchronises subtitles, and [Shoal
AI](https://github.com/chrisgrulau/jellyfin-ai) gives both optional AI help. Each works on its own; installed together,
they help each other.

A [Jellyfin](https://jellyfin.org) plugin that finds subtitles for videos that are missing them, checks every candidate
against what is actually said in the audio, and fixes the timing, so the subtitles you get are the right ones and in sync.

> **Status:** early development. The design is settled ([docs/DESIGN.md](docs/DESIGN.md)); features land release by release.

## What it does

For each video missing subtitles (or holding suspect ones):

```
find candidates → score them → check the best against the audio → synchronise → verify → file (or ask you)
```

1. **Find candidates**: first through Jellyfin's own subtitle providers (for example the OpenSubtitles plugin, using your
   account), then subtitles already embedded in the file (text tracks, and image tracks read with OCR), then optional
   extra providers. As a last resort, and only if you allow it, subtitles can be generated from a transcript (clearly
   labelled as such).
2. **Score them** without spending anything: release name, source and edition, frame rate, running time, uploader
   signals, machine-translation flags, language check.
3. **Check against the audio**: short, dialogue-heavy snippets are transcribed and matched against the subtitle text.
4. **Synchronise**: fixes a constant offset, gradual drift, or separate sections (for example around cuts), using the
   video's own speech/silence pattern first (free) and speech-to-text when needed.
5. **File it**, keeping the original download beside the result, or hold it for your review.

## Speech-to-text

Three uses, each switched on or off separately and each with its own provider and model:

| Use | What | Default |
|---|---|---|
| Check and synchronise | A few short snippets per video | On |
| Context for AI decisions | A slightly longer excerpt, when the [AI plugin](https://github.com/chrisgrulau/jellyfin-ai) is installed | Off |
| Full transcript | The whole video: last-resort subtitles, detailed checks, precise timing | Off |

Providers:

| Provider | Setup | Cost |
|---|---|---|
| **Built-in** (default) | One click to allow it: the plugin then downloads a checksum-verified Whisper program and a small model (about 150 MB) the first time it's needed | Free; CPU, slower |
| **Local service** | A local speech-to-text service (Whisper), with a guided one-line setup on the plugin page | Free; fast with a GPU |
| **Cloud** (Deepgram, OpenAI …) | Paste an API key | Per minute of audio |

## Safety

- **Reversible and idempotent**: the original subtitle is always kept; every change is recorded with where it came from,
  how it was scored and synchronised, and what it cost. Re-running skips work already done.
- **Timing fixes** are applied automatically by default; **changes to the wording** are never applied without your review
  unless you choose otherwise.
- **Clean-up** follows the same rules. Removing subtitle-site adverts and credit lines is automatic by default (they are
  never dialogue; this can be switched to review). Empty lines are always removed. Merging a line repeated back to back
  and removing sound descriptions (off by default) change the wording, so they follow the wording setting. Shortening
  overlaps and lengthening lines too brief to read follow the timing setting. ASS signs, karaoke and effects keep their
  timing unless you ask otherwise.
- **Budgets and limits**: monthly spending limit for paid services, in your own currency (5 a month by default; 0 means
  no paid services at all, and "no limit" is an explicit choice). Providers' charges (usually in US dollars) are
  converted with the European Central Bank's daily rates, with an optional percentage for taxes or card fees, cost estimates before bulk runs, and respectful
  handling of provider limits (no hammering an API that has said stop).

## Settings

**Dashboard → Plugins → Subtitles.** Basic settings cover the key decisions in plain language; an **Advanced settings**
section holds finer controls (confidence thresholds, calibration, matching weights) with warnings where a change could
make results worse.

## Requirements

- Jellyfin **12.1** or newer (the plugin targets .NET 10).
- For OpenSubtitles results: Jellyfin's OpenSubtitles plugin, signed in to your account.

## Roadmap

1. **First release:** candidate sources, scoring, audio check and synchronisation (built-in, local or cloud
   speech-to-text), subtitle clean-up, reversible changes, review screen, budgets.
2. AI tiebreaker when the [AI plugin](https://github.com/chrisgrulau/jellyfin-ai) is installed; audit mode for existing
   subtitles; subtitle editor.
3. Full transcription: last-resort subtitles, discrepancy finder, automatic confidence calibration.
4. More languages; later, subtitles in a different language from the audio.

## Building

The shared source ([jellyfin-plugin-common](https://github.com/chrisgrulau/jellyfin-plugin-common)) is a git
submodule, so clone with `--recurse-submodules`, or fetch it in an existing clone; the build fails without
`external/common`:

```bash
git submodule update --init --recursive
dotnet build -c Release
```

Any .NET 10 SDK builds it (`global.json` sets the floor, so Linux distribution packages work), and package versions are locked in `packages.lock.json`. The Jellyfin
packages are pinned to the server version in `build.yaml`'s `targetAbi`; bump them together.

The output `Jellyfin.Plugin.Subtitles.dll` goes in `<jellyfin data>/plugins/Subtitles_<version>/`.

## Security

This repository is public. **No secrets are committed.** API keys you enter are stored in Jellyfin's plugin
configuration on your server. See [SECURITY.md](SECURITY.md).

## Licence

[GPL-3.0](LICENSE), in line with Jellyfin's official plugins (the server itself is GPL-2.0).
