# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow Jellyfin's plugin versioning.

## [Unreleased]

### Added

- **Built-in speech-to-text.** Once an administrator allows it, the plugin downloads whisper.cpp (built and published
  by this project for Linux x64/arm64, Windows x64 and macOS) and a speech model (`base` by default, or `small`) the
  first time it's needed, and runs it on the server's CPU. Every file is checked against a SHA-256 compiled into the
  plugin, before it is first run and again before every run; downloads come only from this project's releases over
  HTTPS. Runs use below-normal priority, at most 8 threads and a time limit. The settings page has a **Built-in** box
  with **Test** (DOC-02).

## [0.1.0-alpha] - 2026-09-25

### Security
- Subtitle files are read through one entry point (`SubtitleReader.Read`) that refuses anything over 10 MB before
  decoding (SUB-04). Patterns that scan whole files match only spaces and tabs, not newlines, and have a time limit,
  so long runs of blank lines can't slow parsing.

### Fixed
- A UTF-8 byte-order mark in front of text that isn't UTF-8 (common after careless conversions) falls back to the
  legacy code page instead of throwing (SUB-01).
- ASS files with a later malformed `Format:` line, or two different ones, no longer crash the writer or write events
  with the wrong layout (SUB-02). Events are stored in the file's first usable format, and the writer falls back to the
  standard v4+ layout if the format is unusable.
- Written SubRip and WebVTT cues never contain a blank line (which would end the cue early), and `-->` in WebVTT cue
  text is escaped (SUB-07).
- The subtitle language list gained a duplicate "eng" on every restart (Jellyfin's XML loader adds saved items to a
  list's default); it now starts empty (meaning English) and is de-duplicated on save.
- The pipeline cleans subtitles too, in the same single write as any timing correction (one backup, one undo): what the
  settings apply automatically (adverts, empty lines, overlaps, too-brief lines by default) is applied; the rest
  (merging repeats, removing sound descriptions) waits on the plugin page with examples of each change and an Apply
  button. Subtitles that don't match the speech only get the harmless clean-up. Files checked by an earlier version of
  the pipeline are checked once more; a change someone undid is never redone.
- Repeated lines are merged only when the repeat overlaps the line before (a ripping error): chants, echoes and people
  repeating each other are dialogue. Identical overlapping lines are left for that merge rather than trimmed.
- Missing subtitles are found: a daily "Find missing subtitles" task (Scheduled Tasks → Shoal) searches Jellyfin's
  subtitle providers (such as the OpenSubtitles plugin) for films and episodes with no subtitle in a chosen language,
  ranks the candidates, downloads the best few one at a time and checks each against the audio with the same two
  stages; the first that clearly fits is added beside the video (`Name.en.srt`, `.sdh` for hearing-impaired), timing
  corrected and cleaned. Existing files are never replaced; Undo deletes an added file (and it isn't searched for
  again). Downloads are capped per day (default 100) and counted across restarts; a video nothing fits is searched
  again after 30 days; specials come last, since subtitle sites rarely have them. Up to 20 videos per run.

### Added
- Subtitle files: SubRip, WebVTT and ASS/SSA reading and writing. Parsing is tolerant of real-world files (wrong or
  missing cue numbers, stray blank lines, mixed line endings, either millisecond separator). WebVTT keeps its header,
  cue identifiers and settings; ASS keeps script info, styles and every event field, so retiming never loses styling.
- Encoding detection: byte-order marks, strict UTF-8, then Windows-1252 for older files. Output is UTF-8 (with a BOM
  for SubRip and ASS, for players that assume a legacy code page).
- Clean-up, with every change reported for review and undo: advert and credit lines (site promotions anywhere; credit
  wording and web addresses only near the start or end, since they can occur in dialogue), empty cues, back-to-back
  duplicates, small overlaps (larger ones are usually deliberate and left alone), flash cues under 0.5 s, and optional
  stripping of hearing-impaired descriptions and speaker labels.
- Shared source from `jellyfin-plugin-common`, as a submodule compiled into the plugin.
- Candidate sources: an `ICandidateSource` interface, and the first source, Jellyfin's own subtitle providers (e.g.
  the OpenSubtitles plugin with the user's account), searched and fetched without saving anything.
- Candidate scoring before download, with a readable reason for every point: fingerprint match (release-name
  comparisons are skipped, since the subtitle was synced to this exact file), wrong episode (rejected), release group,
  kind of source, streaming service, a different cut (heavily penalised), frame rate (e.g. 25 vs 23.976 fps), machine
  or AI translation, forced-only (rejected), hearing-impaired preference, popularity, unsupported formats (rejected).
- Content checks after download: coverage of the running time, cue density, and a language check of the text itself.
- Release-name tags (group, source, service, resolution, edition, REPACK/PROPER, episode codes), avoiding title words
  that look like tags.
- Repository scaffolding: licence (GPL-3.0), README, design notes, contribution and security policies, CI with pinned
  actions, Dependabot, CODEOWNERS.
- Plugin skeleton targeting Jellyfin 12.1 / net10.0, and a settings page with basic settings (languages, timing and
  wording change policies, speech-to-text per use, monthly budget) and a collapsed advanced section.
- Audio synchronisation, first stage (free, local): six stretches of audio are read with Jellyfin's own ffmpeg, speech
  starts found in each are matched against subtitle line starts at every offset within ±90 s and each common
  frame-rate ratio, and the evidence is added up across stretches. A shift (and frame-rate change) is proposed only when
  the answer clearly stands out; otherwise the subtitles are left for speech-to-text. Calibrated on real videos: 27 of
  27 confident answers right, 48 of 48 mismatched subtitles rejected.

- Speech-to-text providers: any OpenAI-compatible service (a local faster-whisper or whisper.cpp server, or OpenAI) and
  Deepgram, with word timings. Replies are size-limited and checked; failures are classified for retries and never
  show the API key; a key is never sent over plain http except to this machine or the local network.
- Audio synchronisation, second stage: three one-minute snippets are transcribed and matched word for word against the
  subtitles, which settles dense dialogue over music and pins timing to about a tenth of a second (30 of 30 real
  shifts and frame-rate changes recovered, 16 of 16 wrong subtitles rejected). Not yet wired to settings.
### Changed
- Clean-up leaves ASS signs, karaoke and effects alone (SUB-05): timing fixes skip events with positioning, movement,
  karaoke, transform, fade, clip or drawing tags, on a layer above 0, or in a style other than the dialogue style. An
  advanced setting turns this back on. Timing and merge changes record the old and new end time for review.
- Clean-up follows the change policies (SUB-08): advert and credit removal has its own setting (automatic by default),
  empty lines are always removed, merging repeated lines and removing sound descriptions follow the wording setting,
  and timing clean-up follows the timing setting. New settings: basic (remove adverts, remove sound descriptions) and
  advanced (advert policy, merge repeats, fix overlaps, lengthen brief lines with their thresholds, typesetting).
- Spending limit (SUB-03): 0 now means no paid usage at all; "no limit" is a separate checkbox with a warning; the
  default is a US$5 monthly cap, so entering an API key never means open-ended spending.
- Built-in speech-to-text (SUB-06) asks before its first download: the settings page says what is downloaded, how big it
  is and where from, and nothing set to Built-in runs until an administrator allows it. The safety requirements for the
  downloader are set out in `docs/DESIGN.md`.
- Currency: costs and limits are shown and set in a currency of your choice (the euro and the ~30 currencies of the
  European Central Bank's daily rates, including AUD); an advanced setting adds a percentage for taxes or card fees.
  The monthly limit setting is now `MonthlyBudget` in that currency.
- Builds (BLD-01, BLD-03, DOC-01): Jellyfin packages pinned to 12.1.0 with lock files and locked-mode restores; SDK
  pinned in `global.json`; releases carry SHA256SUMS and a build-provenance attestation and are published from a draft;
  the tag must match the plugin version; checkout without persisted credentials; job timeouts; Dependabot follows the
  common submodule and the SDK. `.gitignore` covers test audio and models. README explains the submodule.
- `global.json` accepts any .NET 10 SDK (10.0.100 and later), so the SDKs shipped by Linux distributions (10.0.1xx)
  build it; CI uses the newest .NET 10 SDK, and Dependabot no longer raises the minimum. Package versions stay locked.
- The plugin family is now called **Shoal**: this plugin shows as "Shoal Subtitles". Settings, data and the plugin id
  are unchanged.
- Speech-to-text settings: a local service address, write-only API keys for the local service, Deepgram and OpenAI
  (kept in an owner-only file outside the configuration, never shown again), and a Test button per service that sends
  one second of near-silence and reports the result in plain language. Paid services aren't used when the spending
  limit is 0; the built-in Whisper still waits for permission.
- The pipeline, first version (timing): a daily "Check and sync subtitles" task (Scheduled Tasks → Shoal) checks text
  subtitle files beside films and episodes in the chosen languages, up to 50 per run, skipping ones already checked and
  unchanged. The free line-start stage runs first; free speech-to-text (a local or built-in service) settles unclear
  cases and fine-tunes timing. Corrections follow the timing setting: applied, or held for review. The first original
  is kept in the plugin's data folder; files are only changed if unchanged since they were checked, via a hidden
  temporary file; undo only restores if nobody has edited the file since. Subtitles whose text doesn't match the
  speech (another language, another version, or commentary and notes tracks) are flagged and left alone. Paid
  speech-to-text isn't used by automatic runs until cost tracking is available.
- The settings page shows results (newest first) with Apply and Undo, and a "Check now" button.
