# Design notes

## Pipeline

Each stage is an interface, so providers and strategies can be added without touching the others.

```
ISubtitleSource → ICandidateScorer → (download top N) → IAudioCheck → ISynchroniser → verify → file / review
```

### 1. Sources (in order)

1. **Jellyfin's subtitle providers** (`ISubtitleManager`): uses whatever the server has installed and signed in, e.g.
   the OpenSubtitles plugin and its daily download quota. No credentials of our own.
2. **Embedded tracks**: text tracks extracted directly; image tracks (PGS/VobSub) read with OCR.
3. **Extra providers** (optional, pluggable): e.g. Podnapisi, SubDL.
4. **Generated from a full transcript** (off by default; always labelled as machine-generated).

### 2. Scoring (free)

Release group, source (WEB-DL, BluRay …), streaming service, resolution, edition, REPACK/PROPER; frame rate (23.976 vs
25 fps is the usual cause of drift); last cue vs video running time; cue density; uploader and download signals;
machine-/AI-translated flags; hearing-impaired and forced preferences; language check of the text itself.

### 3. Audio check and synchronisation

- **Speech/silence pattern first**: a voice-activity profile of the audio (computed locally) is aligned with the cue
  intervals. Language-independent and free; fixes most offsets and drift.
- **Speech-to-text when needed**: dialogue-dense windows are transcribed and unique word sequences matched against the
  subtitle text; the fit chooses between a constant shift, linear drift, or piecewise sections (cuts detected at the
  largest subtitle gaps). More windows are sampled when the results disagree.

### Speech-to-text tiers

| Tier | Purpose | Notes |
|---|---|---|
| A: sync snippets | Check and synchronise | A few minutes per video at most |
| B: AI context | Excerpt handed to the AI plugin | Extends tier A's snippets to a target length rather than transcribing afresh |
| C: full transcript | Last-resort subtitles, discrepancy finder, precise timing | Opt-in; costly with cloud providers |

Each tier has its own on/off switch, provider, model and budget. Transcripts are cached by file fingerprint, provider,
model and time range, so no audio is paid for twice and re-runs are free.

### Providers

- **Built-in**: a whisper.cpp CPU build that this project compiles in CI for Linux (x64, arm64), Windows and macOS and
  publishes with checksums, plus a small model; downloaded on first use and run on demand. No setup beyond a one-time
  permission (see below).
- **Local service**: anything speaking the OpenAI transcription API (for example speaches, faster-whisper-server or the
  whisper.cpp server). The plugin page detects services on the usual ports, reads Jellyfin's hardware-acceleration
  setting to suggest the right GPU build, shows a copy-paste setup command, and has a Test button. The plugin never
  installs system services itself.
- **Cloud**: Deepgram, OpenAI (and more over time), each with an API key.

### Built-in speech-to-text: safety requirements

Downloading and running a native program is the riskiest thing this plugin family does, so the built-in provider must
meet all of these before it ships:

- **Explicit permission first.** Nothing is downloaded until an administrator allows it on the settings page, which says
  what will be downloaded, roughly how big it is and where it comes from (`AllowBuiltInDownload`, off by default).
  Until then, anything set to Built-in waits.
- **Checksums compiled in.** The SHA-256 of every program and model is a constant in the plugin DLL, never fetched
  alongside the file.
- **One fixed origin.** HTTPS to this project's GitHub releases only; redirects to any other host are refused.
- **Verify before it can run.** The file is downloaded to a temporary name, verified, and only then marked executable
  and moved into a plugin-owned folder that other users can't write to. It is verified again every time it starts.
- **No shell.** The process is started with `ProcessStartInfo.ArgumentList`; media paths are always absolute, so a file
  name starting with `-` can't be read as an option.
- **Bounded.** A timeout per job, the whole process tree killed on cancel, a capped thread count and low priority.

## Decisions and confidence

- Timing fixes: automatic by default. Text changes: review by default; never silent.
- A subtitle line is flagged only when the transcript is confident **and** the difference is meaningful (names, numbers,
  negations, missing lines). With the AI plugin installed, it judges meaning vs wording.
- Agreement between independent sources outweighs a single model's confidence (default; can be switched to review).
- Confidence is not comparable across models, so thresholds are per model (starting points: Deepgram word confidence
  ≥ 0.90 over a line; Whisper average log-probability ≥ −0.3 with compression-ratio and no-speech guards) and
  self-calibrated by default against subtitles already known to be good.

## Reversibility and provenance

The original subtitle is always kept. A small JSON record next to each result stores source, scores, sync model and
parameters, providers used and cost, so any change can be undone and re-runs are idempotent.

## Budgets, limits and failures

Shared with the other plugins through
[jellyfin-plugin-common](https://github.com/chrisgrulau/jellyfin-plugin-common): failure classes with their own retry
behaviour, provider-stated reset times, spending caps per service and purpose, estimates before bulk runs, and
approve-first or automatic scheduled runs.

Spending limits: 0 means **no paid usage** (cloud providers are never called); unlimited is a separate, explicit choice
with a warning; the default is a small cap (US$5 a month), so entering an API key never means open-ended spending.
When budgets are enforced, the estimated cost of each call is reserved before it is made, atomically across concurrent
jobs, and the actual cost is settled afterwards, so parallel jobs can't overshoot the limit together.

## Working with the other plugins

Plugins never share C# types. The Subtitles plugin offers a JSON-in/JSON-out entry point (speech-to-text for Ingest, e.g.
to tell episodes apart), and calls the AI plugin the same way when it is installed. Without the AI plugin everything
works, just without AI tiebreakers.

## Settings: basic vs advanced

Basic settings are the key decisions in plain language. Advanced settings (thresholds, calibration, snippet lengths,
matching weights) sit behind a collapsed section with a warning; risky values show their own warning.

## Languages

Same-language subtitles for any language the providers support. Different audio and subtitle languages are on the
roadmap: identify both languages, transcribe, translate (Whisper can translate straight to English), and synchronise
mainly on the speech/silence pattern, with fuzzy text matching as a secondary signal.
