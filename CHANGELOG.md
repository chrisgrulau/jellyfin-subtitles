# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow Jellyfin's plugin versioning.

## [Unreleased]

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
- Repository scaffolding: licence (GPL-3.0), README, design notes, contribution and security policies, CI with pinned
  actions, Dependabot, CODEOWNERS.
- Plugin skeleton targeting Jellyfin 12.1 / net10.0, and a settings page with basic settings (languages, timing and
  wording change policies, speech-to-text per use, monthly budget) and a collapsed advanced section.
