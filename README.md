# Shoal Subtitles

<p align="center"><img src="assets/shoal-subtitles.png" alt="Shoal Subtitles icon" width="480"></p>

Part of **Shoal**, a family of Jellyfin plugins that work together: [Shoal
Ingest](https://github.com/chrisgrulau/jellyfin-ingest) files new media into your libraries, [Shoal
Subtitles](https://github.com/chrisgrulau/jellyfin-subtitles) finds, checks and synchronises subtitles, and [Shoal
AI](https://github.com/chrisgrulau/jellyfin-ai) gives both optional AI help. Each works on its own; installed together,
they help each other.

A [Jellyfin](https://jellyfin.org) plugin that finds subtitles for videos that are missing them, checks every candidate
against what is actually said in the audio, and fixes the timing, so the subtitles you get are the right ones and in sync.

> **Status:** alpha. Checking and fixing existing subtitles, and finding missing ones, work with the built-in
> speech-to-text, a local service, or a paid service within your monthly limit. The design is in
> [docs/DESIGN.md](docs/DESIGN.md).

## Installing

**From the Shoal plugin repository (recommended):** in **Dashboard → Plugins → Repositories**, add
`https://raw.githubusercontent.com/chrisgrulau/jellyfin-shoal/main/manifest.json`, install **Shoal Subtitles** from
the catalogue and restart Jellyfin. Jellyfin installs updates from a repository automatically (daily, and at start-up)
unless you switch that off for the plugin under **My Plugins**.

**By hand:** download `jellyfin-plugin-subtitles.zip` from the [releases](https://github.com/chrisgrulau/jellyfin-subtitles/releases),
check it against `SHA256SUMS` (and, if you like, its build provenance with
`gh attestation verify jellyfin-plugin-subtitles.zip --repo chrisgrulau/jellyfin-subtitles`), and put
`Jellyfin.Plugin.Subtitles.dll` in `<jellyfin data>/plugins/Subtitles_<version>/`, then restart Jellyfin.

Nothing is checked, changed or downloaded until you have saved the settings page once. For speech-to-text,
either allow the **built-in** one on the plugin page (it downloads about 90 MB the first time), or point **Local service
address** at an OpenAI-compatible service (for example a faster-whisper server); then press **Test**.

## What it does

For each video missing subtitles (or holding suspect ones):

```
find candidates → score them → check the best against the audio → synchronise → verify → file (or ask you)
```

1. **Find candidates**: first through Jellyfin's own subtitle providers (for example the OpenSubtitles plugin, using your
   account), then SubDL (with a free API key). Text subtitles already inside the video can be checked too (optional).
   Planned, not yet built: image tracks read with OCR, and subtitles generated from a full transcript as a last resort.
2. **Score them** without spending anything: release name, source and edition, frame rate, running time, uploader
   signals, machine-translation flags, language check.
3. **Check against the audio**: short, dialogue-heavy snippets are transcribed and matched against the subtitle text.
4. **Synchronise**: fixes a constant offset or a gradual drift (a frame-rate difference), using the video's own
   speech/silence pattern first (free) and speech-to-text when needed. Separate offsets around cuts are planned.
5. **File it**, or hold it for your review. When an existing file is changed, its original is kept in the plugin's data
   folder, so **Undo** brings it back.

## Speech-to-text

Three uses, each switched on or off separately and each with its own provider and model:

| Use | What | Default |
|---|---|---|
| Check and synchronise | A few short snippets per video | On |
| Context for AI decisions | A slightly longer excerpt, when the [AI plugin](https://github.com/chrisgrulau/jellyfin-ai) is installed. Also used for the short transcripts [Ingest](https://github.com/chrisgrulau/jellyfin-ingest) may ask for (**Let Ingest ask for short transcripts**, off by default) to tell which episode a new video is | Off |
| Full transcript | Coming later: the whole video, for last-resort subtitles and detailed checks. Not used yet; the settings page shows it disabled | Off |

Providers:

| Provider | Setup | Cost |
|---|---|---|
| **Built-in** (default) | One click to allow it: the plugin then downloads a checksum-verified Whisper program (whisper.cpp, built by this project for Linux x64/arm64, Windows x64 and macOS) and a model, about 90 MB (`base`, the default) or 200 MB (`small`), the first time it's needed | Free; CPU, slower |
| **Local service** | A local speech-to-text service (Whisper), with a guided one-line setup on the plugin page | Free; fast with a GPU |
| **Cloud** (Deepgram, OpenAI …) | Paste an API key | Per minute of audio |

## How it works today

A daily task (**Scheduled Tasks → Shoal → Check and sync subtitles**, or **Check now** on the plugin page) checks the
text subtitle files beside your films and episodes. Each one's timing is compared with the audio, first for free by
matching where lines start against where speech starts, then, if that isn't clear-cut, by transcribing a few minutes
with a free local speech-to-text service and matching the words. Corrections (a shift, and a frame-rate change if the
subtitle was made for a PAL release) are applied or held for your review, and every change can be undone from the
plugin page.

A second daily task (**Find missing subtitles**, or **Find missing now**) searches your subtitle providers, such as the
OpenSubtitles plugin, for films and episodes that have no subtitle in your languages. The best candidates are
downloaded one at a time and checked against the audio the same way; one is added only if it clearly fits, with its
timing corrected. Existing subtitle files are never replaced, downloads are capped per day, and Undo removes an added
subtitle.

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
  converted with the European Central Bank's daily rates, with an optional percentage for taxes or card fees.
  - Every paid call is priced from the providers' published prices (shipped with the plugin), reserved against the
    month's limit before it is made, and recorded afterwards, so runs stop at the limit.
  - A call whose cost can't be worked out isn't made.
  - The settings page shows this month's spending.
  - Provider limits are handled respectfully: no hammering an API that has said stop.

## Settings

**Dashboard → Plugins → Subtitles.** Basic settings cover the key decisions in plain language; an **Advanced settings**
section holds finer controls (costs, how much is checked per run, clean-up details, AI checks) with warnings where a
change could make results worse. Automatic threshold tuning and "Let agreement between sources settle disagreements" are
shown there as coming later; they have no effect yet.

## Requirements

- Jellyfin **12.1** or newer (the plugin targets .NET 10).
- For OpenSubtitles results: Jellyfin's OpenSubtitles plugin, signed in to your account.
- For SubDL results: a free API key from subdl.com, entered on the plugin page.
- **Write access to your media folders** for Jellyfin's account: corrections are written beside the video, and found
  subtitles are added there. Folders it can't write (a read-only mount, NAS permissions) are shown as "Can't write here"
  and tried again after 30 days, without downloading or checking anything.
- Jellyfin's ffmpeg (it comes with the official packages and images; a system ffmpeg found on the PATH works too).

## Roadmap

1. **First release:** candidate sources, scoring, audio check and synchronisation (built-in, local or cloud
   speech-to-text), subtitle clean-up, reversible changes, review screen, budgets.
2. With the [AI plugin](https://github.com/chrisgrulau/jellyfin-ai) installed: lines matched by meaning when the
   wording differs from what is said (done); wording audit of checked subtitles and, a few per night, the existing library (done);
   subtitle editor with audio playback (done).
3. Full transcription: last-resort subtitles, discrepancy finder, automatic confidence calibration.
4. More languages; later, subtitles in a different language from the audio.

## What it stores and sends

**Stored** in the plugin's data folder (`<jellyfin data>/plugins/Jellyfin.Plugin.Subtitles/`): `keys.json` (owner-only),
`results.json` (a result per subtitle: paths, video names, what was changed; kept while the file exists),
`originals/` (the original of every file it changed, for Undo), `spend.json` and `rates.json` (spending), and
`downloads.json` (the day's download count). The built-in speech-to-text lives in `<jellyfin data>/shoal-subtitles/`.

**Sent:**

| To | When | What |
|---|---|---|
| Your subtitle providers (through Jellyfin), SubDL | Finding missing subtitles | The video's title, year, season and episode, as Jellyfin's own search does |
| A cloud speech-to-text service | Only if you chose one | A few one-minute audio snippets per checked file (the built-in and local services keep audio on the server) |
| Shoal AI → your AI provider | Only if installed and allowing Subtitles | The subtitle language, a few minutes of heard phrases and the subtitle lines around them |

**Needs write access** to your media folders: corrections are written beside the video.

## Upgrading and uninstalling

Upgrades keep settings, results and originals. Before uninstalling, use **Undo** on any change you want reversed: once
the plugin is gone, its originals are no longer linked to their files. Left behind: the plugin's data folder (keys,
results, originals) and `<jellyfin data>/shoal-subtitles/` (the built-in speech-to-text). Jellyfin's own "Download
missing subtitles" task uses the same OpenSubtitles allowance, so you may want only one of them searching.

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

This repository is public. **No secrets are committed.** API keys you enter are stored in their own file,
`keys.json` in the plugin's data folder, readable only by Jellyfin's account (never in the plugin configuration, and
never shown again or logged). See [SECURITY.md](SECURITY.md).

## Licence

[GPL-3.0](LICENSE), in line with Jellyfin's official plugins (the server itself is GPL-2.0).
