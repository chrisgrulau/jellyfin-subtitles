# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow Jellyfin's plugin versioning.

## [Unreleased]

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
