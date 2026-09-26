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

- **Speech starts first (free, local, any language)**: six two-minute stretches spread across the video are read
  with Jellyfin's own ffmpeg (16 kHz mono, single-threaded, low priority, time-limited, no shell). In each, the points
  where speech-band loudness rises sharply are compared with where subtitle lines start, at every offset within ±90 s
  (10 ms steps, by FFT) and at each common frame-rate ratio (same rate; PAL speed-up or slow-down between 23.976, 24 and
  25 fps). The stretches' score curves are **added up**, so all the evidence counts even when no single stretch is
  decisive. Line starts beat a speech/silence profile: in dense dialogue subtitles cover nearly all the time, but line
  starts still vary.
  - A correction is made only when the combined peak clearly stands out: a margin of at least 0.03 over any offset
    more than 2 s away, **and** at least 6 standard deviations above the curve. Calibrated on real library videos:
    subtitles deliberately paired with the wrong episode or film never passed (48 of 48 rejected); correctly paired
    ones that passed were always right, shifts and frame-rate changes included (27 of 27); the rest (dense comedy over
    music or a laugh track) are left for speech-to-text rather than risk moving good subtitles.
  - The detector registers speech starts about 0.24 s after the line starts of subtitles known to be in sync; that lag
    is subtracted. Offsets under 0.5 s at the same frame rate are left alone: line starts don't pin timing down more
    finely than that, and speech-to-text does.
- **Speech-to-text when needed**: three one-minute snippets are transcribed with word timings, chosen where the
  subtitles have the most dialogue and at least a tenth of the video apart. Every run of three words that occurs exactly
  once in both the transcript and the subtitles is an anchor (subtitle time, audio time). For each frame-rate ratio the
  anchors' offsets are clustered; the densest cluster wins, a simpler ratio being kept unless another fits clearly
  better (24 and 23.976 fps can't be told apart over a short video). The precise offset is the median over anchors that
  begin a cue, whose subtitle time is exact. A correction needs at least 8 agreeing anchors and 40 % of all of them;
  shifts under 0.2 s are left alone. Calibrated on real videos with a local faster-whisper service: shifts and frame-rate
  changes recovered in 30 of 30 cases, including every dense comedy the line-start stage had to leave; subtitles for
  another episode or film rejected in 16 of 16; about 10 s per video on a small GPU. Word matching also catches
  subtitles in the wrong language (nothing matches), which the language-independent stage can't.
  Piecewise corrections (cuts) come later, when anchors split into clusters along the timeline.

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

How it meets them: the program comes from this repository's `whisper-v*` releases (built by the **Whisper**
workflow, whisper.cpp pinned by commit; models pinned by revision and SHA-256), and `tools/builtin_checksums.py` writes
every file's SHA-256 into `BuiltInRelease.Checksums.cs`. `BuiltInInstaller` follows redirects itself, only over HTTPS and
only to GitHub's download hosts; streams each file to a temporary name with a size cap while hashing it; requires a zip
to hold exactly the listed files, as plain names; installs into a `0700` folder in the plugin's data folder; and hashes
every file again before each run, fetching anything that no longer matches. `BuiltInSpeechToText` runs it with
`ArgumentList`, absolute paths, half the CPUs (at most 8), below-normal priority and a time limit of 2 minutes plus 5×
the audio length, and kills the process tree on cancel. Linux and Windows builds carry every CPU variant (SSE4.2 up to
AVX-512 / SVE2) and pick one at run time. Word times come from whisper.cpp's DTW token timings, less 0.28 s, which
lines them up with the local-service word times the synchroniser was calibrated on.

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
- Lines matched by meaning (stage 3 of the audio check):
  - **When:** speech-to-text heard at least 40 words, but exact three-word matches couldn't settle the timing.
  - **What the line matcher gets:** the heard words grouped into phrases (split at pauses of 0.7 s, sentence ends, or
    every 14 words), and the subtitle lines within 150 s of each heard stretch (at most 300). The AI plugin is the
    matcher, with purpose `subtitles.lines` and at most `MaxAiChecksPerRun` questions per task run.
  - **What it answers:** "same", with pairs; "different"; or "unsure".
  - **Pairs:** each pair must name an offered phrase and line and neither may be reused. Each becomes an anchor (line
    start against phrase start), and the usual solver needs at least 6 of them agreeing within 0.5 s on one shift and
    frame-rate ratio. Pairs that don't agree change nothing.
  - **"Different":** confirms "another language / something else".
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
with a warning; the default is a small cap (5 a month in the chosen currency), so entering an API key never means
open-ended spending. Limits are set and costs shown in the user's currency; each charge is recorded in the currency it
was made in and converted as described in jellyfin-plugin-common's *Currencies* notes (ECB daily rates; unknown rates
pause paid calls in other currencies rather than guess), plus an optional percentage for taxes or card fees.

How it's done: `Pricing/prices.json` holds the providers' published prices (per audio minute, dated, validated as a
whole). `MeteredSpeechToText` wraps a paid service: it prices each call from the audio length, reserves it in the
shared `SpendLedger` against the month's limit (in the user's currency, with the ECB rates from `ExchangeRateStore`),
settles it after the call, or releases it if the call failed. A call is refused if the price is unknown, the rates are
missing or stale, or it would go over the limit; the run then carries on with the free line-start stage.
When budgets are enforced, the estimated cost of each call is reserved before it is made, atomically across concurrent
jobs, and the actual cost is settled afterwards, so parallel jobs can't overshoot the limit together.

## Working with the other plugins

Plugins never share C# types. The Subtitles plugin offers a JSON-in/JSON-out entry point (speech-to-text for Ingest, e.g.
to tell episodes apart), and calls the AI plugin the same way when it is installed. Without the AI plugin everything
works, just without AI tiebreakers.

`Bridge.SpeechBridge.TranscribeAsync(string json, CancellationToken)` is found by name by the shared
`SpeechBridgeClient`, the same way as the AI plugin's entry point.

- **Request (version 1):** `caller`, `purpose`, `path`, `start`, `length` (seconds) and optionally `language`.
- **Reply:** `text`, `language` and `provider`, or `error` and a `failure` name (`not-allowed`, `not-set-up`,
  `authentication`, `provider-limit`, `transient`, `bad-request`, `no-connection`).
- **Checks:**
  - The plugin must be on, and the caller must be allowed (only `ingest`, with **Let Ingest ask for short
    transcripts**).
  - The purpose must start with the caller's name.
  - The "Context for AI decisions" tier must be on.
  - The path must be absolute and the file must exist.
  - The stretch must start at 0 or later and be longer than 0 and at most 180 seconds.
  - The language, if given, must be a two- or three-letter code.
- **Transcribing:** the audio is read with Jellyfin's ffmpeg (first audio track, 16 kHz mono), as for snippets. The
  tier's service transcribes it, and a paid service is wrapped in `MeteredSpeechToText` under the caller's purpose. A
  semaphore lets only one transcription run at a time, so the built-in service never runs twice at once.

## Settings: basic vs advanced

Basic settings are the key decisions in plain language. Advanced settings (thresholds, calibration, snippet lengths,
matching weights) sit behind a collapsed section with a warning; risky values show their own warning.

## Languages

Same-language subtitles for any language the providers support. Different audio and subtitle languages are on the
roadmap: identify both languages, transcribe, translate (Whisper can translate straight to English), and synchronise
mainly on the speech/silence pattern, with fuzzy text matching as a secondary signal.
