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
4. **Generated from a full transcript** (off by default; always labelled as generated; see
   [Generated subtitles](#generated-subtitles)).

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
| C: full transcript | Last-resort subtitles and the whole-file check; later precise timing | The whole video in 10-minute chunks; used by [Generated subtitles](#generated-subtitles) and the [Whole-file check](#whole-file-check) |

Each tier has its own on/off switch, provider, model and budget. Full transcripts are cached (`TranscriptCache`, in
`<plugin data>/transcripts/`) by the video file (path, size and time written), audio stream, language, service and model
(`SetupOf`, e.g. `builtin/base`), so no whole video is paid for or transcribed twice: generating and the whole-file check
share them, and a second check is free. Each is a gzipped JSON object with a word per compact array (text, start, end,
confidence, times to the millisecond; a two-hour film comes to a few hundred kilobytes);
the folder is kept under 200 MB, the least recently used (read or written) going first; a damaged file is deleted and
transcribed again. Sync snippets aren't cached: a file is only checked again when it (or the service) changed.

### Providers

- **Built-in**: a whisper.cpp CPU build that this project compiles in CI for Linux (x64, arm64), Windows and macOS and
  publishes with checksums, plus a small model; downloaded on first use and run on demand. No setup beyond a one-time
  permission (see below). On the settings page, **Download now** or **Test** starts the download in the background
  (`POST Subtitles/BuiltIn/Download`; one at a time, sharing the installer's lock) and the page polls its progress
  (`GET Subtitles/BuiltIn/Download`: idle, downloading, verifying, installed or failed, with bytes and percent); Test
  answers at once while it runs. A nightly run that needs it downloads it inline instead (or waits for the download
  already running, then uses it); its progress shows on the page too. A download is cancelled when the server stops.
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
to hold exactly the listed files, as plain names; installs into a `0700` folder under Jellyfin's data folder (`<data>/shoal-subtitles/builtin`, never under `plugins/`, where Jellyfin would load its DLLs as a plugin; an install from an earlier version is moved there on start); and hashes
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

## Generated subtitles

Stage 4, part 1: the last resort when no subtitle can be found.

- **Switch:** `GenerateMissing` (off by default, and nothing runs on a new install until the settings page is saved once,
  as for every task). It needs the Full transcript tier switched on; the page switches the tier on with it.
- **Where it runs:** its own scheduled task, **Generate missing subtitles and check whole files** (`ShoalSubtitlesGenerate`, daily at 05:00, an
  hour after the search), rather than a step of the search: transcribing whole videos can take hours on a CPU, and the
  search and its **Find missing now** button shouldn't wait for it. It respects cancellation between and within videos.
- **Which videos** (`SubtitleGenerator.NeedsGeneration`): the library walk's "missing" list (`LibraryVideos.Missing`,
  where a generated file doesn't count), restricted to videos whose search result is `NotFound`, in a wanted language
  that is the audio's language (`AudioMatches`: the chosen audio stream's tag, or the first wanted language when the
  stream has no tag or `und`). Not when a `*.generated.*` file is already there, or the video has a generation result:
  `Generated` (even if its file was deleted by hand), `Undone` and `Replaced` never come back on their own; `NoSpeech`
  comes back only when the service/model (`SetupOf`, e.g. `builtin/base`) changes; `Failed` after 3 days; `CantWrite`
  after 30. Ordered by the time of the search result (oldest first), then path; at most `MaxGeneratedPerNight` (20,
  0–200) a night, counting every video transcribed. A time budget, `MaxGenerateHours` (4, 0–24, 0 = none), stops the
  run starting new videos once that long has passed since it began (`SubtitleGenerator.RunAsync`, on the injected
  clock); the video in progress finishes within its own limit, and the summary line says "stopped after 4 h; N left for
  tomorrow".
- **Service:** the Full transcript tier (`FullTranscript`: provider and model), separate from the snippet and AI-context
  tiers; built-in by default, which needs its usual download permission. Built with `forSubtitles`: Deepgram is asked
  to punctuate (`punctuated_word`), OpenAI-compatible services for segments as well as words (OpenAI's words have no
  punctuation, so each segment's punctuated tokens are laid onto its words; a service that gives segments without word
  times has words spread over each segment by length). Checks keep their plain requests.
- **Metering:** a paid service reserves the cost of the whole video's audio (all chunks, overlaps included) before the
  first chunk (`MeteredSpeechToText.RunWholeAsync`), so a video isn't left half-paid at the limit, and settles at the
  audio actually sent, even when a chunk fails or the run is cancelled part-way. A refusal (limit, unknown price, stale
  rates) or a provider limit/sign-in failure stops the night's run without recording a result.
- **Audio and chunks:** read with Jellyfin's ffmpeg as for snippets (the language's audio stream, 16 kHz mono,
  single-threaded, 2-minute limit per read), 10 minutes at a time with a 5-second overlap (`TranscriptChunks`). 10
  minutes of 16-bit WAV is about 19 MB, under the 25 MB upload cap OpenAI-compatible services commonly have. The
  built-in whisper.cpp and Deepgram could take a whole film, but the plugin holds audio as float samples, so a two-hour
  film in one piece would be about 460 MB of samples plus a 230 MB WAV; every service gets the same chunks instead.
  Chunk times are moved to the video's clock; a word is kept from the chunk on whose side of the middle of the overlap
  its midpoint falls, and the same word heard by both chunks at the seam (starting within a second) is kept once.
- **Time limit:** 10 minutes plus 5× the video's length per video, at most a day (each built-in run also has its own
  limit, and each HTTP call 20 minutes rather than the usual 3, for a local service on a CPU); a video that runs over is
  recorded as failed.
- **Cues** (`TranscriptCues`, a pure function): word timings (every provider gives them; segments only fill in as above).
  Sound descriptions (`[Music]`, `(laughs)`, bracketed runs), music notes and words without letters or digits are
  dropped. Words are grouped greedily: a new cue at a pause over 0.6 s, after a sentence end once the cue has 12
  characters, or when the next word would make it longer than 7 s or more than two lines of 42 characters (then split
  after a comma, semicolon, colon or dash in its second half, if there is one). Lines are balanced, preferring a break
  after punctuation. A cue runs from its first word's start to its last word's end, lengthened into the following
  silence towards 1 s and towards 20 characters a second, never beyond 7 s, and ends at least 80 ms before the next cue
  (words packed closer than that push the next cue on). Chinese, Japanese and Korean words are joined without spaces.
- **Quality guard:** fewer than 20 words an hour (at least 3) after dropping sound descriptions records `NoSpeech`
  ("No speech to transcribe") and writes nothing.
- **File and naming:** `<video name>.<two-letter language>.generated.srt` beside the video (`SubtitleGenerator.PathFor`),
  UTF-8 SubRip with a byte-order mark, created without overwriting. Jellyfin's external-file parser
  (`Emby.Naming.ExternalFiles.ExternalPathParser`, checked against the 12.1 package) splits the suffix at dots: `en`
  becomes the language, `generated` matches none of its flags (`default`; `forced`, `foreign`; `sdh`, `cc`, `hi`) and
  becomes the stream's title, so `MediaStream.DisplayTitle` reads "generated - English - SRT - External". Nothing is
  added to the subtitle text.
- **"Has a subtitle":** `FindRules.Counts(…, isGenerated)` says a generated file (`SubtitleGenerator.IsGenerated`: the
  name ends in `.generated` before the extension) never counts, so the search goes on; the library walk leaves
  generated files out of the timing check (they are speech-to-text already). Regeneration is stopped by the file and by
  the result, not by the walk.
- **Replacement:** when the search adds a subtitle for a video and language with a `Generated` result, the generated
  file is copied to `originals/` and deleted if it is still as generated (one edited by hand is left in place), and the
  result becomes `Replaced`. The generator also checks, just before writing, that the search hasn't found one
  meanwhile.
- **Results:** id `gen-` + hash of video path and language, separate from the search's `find-` result (so the search's
  30-day rhythm is untouched). `Generated` is `Changed`, so **Undo** removes the file if unchanged (`Undone`); the editor
  doesn't open generated subtitles (Undo would no longer apply). Results record the video's path and are dropped only
  with the video, so a generated file someone deleted isn't made again. `Generated` goes to the Activity log like
  `Added`.

## Whole-file check

Stage 4, part 2: a doubtful subtitle compared line by line with a full transcript of its video. Differences are flagged
for review, never applied on their own.

- **Switch:** `CheckWholeFile` (off by default) for doubtful subtitles; **Check whole file** in the results
  (`POST Subtitles/Results/{id}/CheckWholeFile`) queues any subtitle file (`SubtitleResult.WholeFileRequested`) whatever
  the switch, answering at once. A file the run could never check is refused with 400 and the reason, which the page
  shows, by the run's own rules (`WholeFileChecker.Ineligible`): generated subtitles, embedded tracks, results without a
  file, subtitles matched by meaning, a subtitle its video in the library doesn't list, and a language that isn't wanted
  or isn't the audio's. Should a queued file become unreachable later (the settings or library changed), the run clears
  its flag and notes why in its result (`ClearUnreachable`). Needs the Full transcript tier; the page switches it on with
  the switch.
- **Where it runs:** a step of the full-transcript task (`ShoalSubtitlesGenerate`, 05:00), sharing its time budget
  (`MaxGenerateHours`, counted from when the task began) so whole videos are transcribed one at a time: first the files
  asked for (someone is waiting), then generation, then doubtful files. At most `MaxWholeFileChecksPerNight` (5, 0–200)
  a night, the ones asked for counted too; purpose `subtitles.wholefile` for metering. A paid service is reserved for
  the whole video before it starts, as for generating; a limit or sign-in refusal stops the night.
- **Which files** (`WholeFileChecker.Choose`, `IsDoubtful`): subtitle files from the library walk (never `*.generated.*`),
  whose result (its own, or the search's for one it added) is asked for; or, with the switch, doubtful: `Unreliable`
  ("Unclear"), or settled by speech-to-text with confidence under 0.5 (few agreeing words), or carrying wording-audit
  findings; not matched by meaning (translations), not generated or embedded; in the audio's language (the chosen audio
  stream's tag, or the first wanted language for an untagged one); not checked whole before, except a failed transcript
  after 3 days. Asked-for first, then oldest result first. A file that changed since its result isn't compared (the
  nightly check sees it first); other languages, unreadable or badly decoded text are noted and not compared.
- **Alignment** (`DiscrepancyFinder`, pure and deterministic): the file's times are moved onto the audio's clock by a
  correction waiting for review (`Proposed`), otherwise taken as they are. Each subtitle word may match an equal heard
  word within ±3 s of its line (`Tolerance`); the longest run of matches in order on both sides wins (Hunt–Szymanski:
  the longest increasing subsequence of the candidate pairs). Heard words between a line's first and last match are that
  line's. Each line is then moved by the median offset of the matches within a minute of it (clamped to ±3 s); heard
  words left between lines' matches go to a line they are heard during (0.5 s padding), keeping order; a line with no
  matches takes the unclaimed words within ±3 s nearer to it than to its neighbours.
- **Normalising** (`SpokenText`): markup, sound descriptions in brackets, music notes and upper-case speaker labels
  removed; case and punctuation ignored; English contractions split (`don't` → `do not`, `can't` → `can not`, `'s` →
  `is` on both sides); numbers in words become digits (`twenty-five` 25, `one hundred and five` 105, `nineteen ninety`
  1990, `a thousand` 1000; a comma ends a number, so "two, three" stays two), digit separators dropped. Names are
  capitalised words where a sentence doesn't start (not "I"); not in German, or in text all in one case.
- **Findings** (one per line; kinds, most important first):
  - `negation`: the line and what is heard for it have different numbers of `not`/`no`/`never`/`nothing`/`nobody`/
    `none`/`neither`/`nor`/`nowhere`, and so do the line with its neighbours.
  - `number`: a number in the line not heard for it or its neighbours, or heard but not in the line or its neighbours
    (a line break in another place isn't a difference). With one on each side, the fix replaces it in place.
  - `name`: a word heard in the place of another (in the aligned line) where either is a name, and neither appears on
    the other side nearby. With one, the fix replaces it in place.
  - `words`: at least 4 heard words, over half of those heard for the line, aren't in it or its neighbours.
  - `missing-line`: heard speech (split at pauses over 1 s) of at least 4 words and 1.5 s that no line is shown during;
    the fix adds it, from its first word to its last (at least 1 s, ending before the next line).
  - `extra`: a line of at least 3 spoken words with nothing heard within ±3 s; music (♪, ♫, `#`) and sound descriptions
    are never flagged. The fix removes it.
  - Otherwise the fix is the heard words as a line (a capital first, wrapped at 42 characters).
- **Guards:** with at least 20 subtitle words and fewer than 25 % of them heard, or with lines with nothing heard more
  than a quarter of the spoken lines (at least 4), the transcript is taken to be at fault (another version or language,
  music, the wrong audio track) and nothing is flagged; the result says so. At most 50 findings are kept (the counts
  cover them all).
- **Confidence:** a finding resting on heard words below the service's threshold is dropped (the result says how many):
  the heard number or name, the heard negation, or the mean of the words behind a missing line or missing words.
  Services that give no confidence (OpenAI's whisper-1) aren't second-guessed. Thresholds: see
  [Decisions and confidence](#decisions-and-confidence).
- **AI confirmation (optional):** with the AI plugin, `UseAi`, `AuditWording` and AI checks left in the run, the lines
  flagged for their wording (up to 30; not missing lines or lines with nothing heard) are offered to the wording
  auditor (`subtitles.audit`) with what was heard for each, as one question. Lines it doesn't flag are dropped; no
  answer keeps them all. Within `MaxAiChecksPerRun`, shared with the rest of the task's run.
- **Results:** the findings are `LineFinding`s with `From` = `whole file`, `Heard`, and for a missing line `End`, times
  and text as in the file; they replace the previous whole-file findings and keep the audit's. `WholeFile` records when,
  the service, the counts by kind and a summary, which also ends the explanation ("Whole file checked against a full
  transcript by …: 1 number, 1 missing line — waiting for review"). Whole-file findings make a result wait for review;
  the results list filters **Differs from what is said (whole file)** and **Whole-file check queued**; the Activity log
  says "Lines of a subtitle differ from what is said (whole file)".
- **Review** (`DiscrepancyReview`; `POST Subtitles/Results/{id}/Findings/{index}/Apply|Decline?time=`): each finding is
  applied (wording replaced, missing line added styled like the first line, or line removed) or declined on its own;
  the time must match, so a stale page can't act on another finding, and a line is found again by its text and time
  (within 1.5 s), so a changed line is never touched. **Apply** on the result applies every finding with a fix, then any
  timing and held-back clean-up, and keeps lines with nothing heard for review (Apply refuses when only those are left);
  **Decline** clears them all. The editor opens at a finding with the fix filled in (a missing line added), saved only
  with Save; saving closes the whole-file findings it dealt with. Applied fixes count as `FixedFromWholeFile`; Undo
  restores the original.

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
- Wording audit:
  - **When:** the timing is settled (in sync, corrected or proposed) and speech-to-text ran. It is skipped for
    subtitles matched by meaning, which are translations.
  - **What the auditor gets:** the heard phrases, and the lines shown during the same stretches mapped to the audio's
    clock. The AI plugin is the auditor, with purpose `subtitles.audit`.
  - **What it answers:** lines of kind `name`, `number`, `negation`, `missing`, `wrong` or `extra`, each with a
    suggestion and a reason.
  - **Which findings are kept:** only for offered lines, of a known kind, one per line, at most 10. An empty or
    unchanged suggestion only flags the line.
  - **How a finding is stored:** as `LineFinding` (the line's time and text as in the file) on the result, which
    makes it wait for review.
  - **Apply:** changes only lines whose text is unchanged and whose time is within 1.5 s. It then retimes a proposed
    correction and applies held-back clean-up, and Undo restores the original.
  - **Earlier subtitles:** after each sync run, up to `MaxAuditsOfEarlierPerRun` results checked before the audit
    existed are audited, oldest first. To qualify, a result must be in sync or corrected, not by meaning, not audited,
    at the current pipeline version, with nothing pending and the same fingerprint.
    - `TranscriptSynchroniser` transcribes again, and the audit runs only if it finds the file in sync. Otherwise the
      result is marked audited with a note.
    - No answer, such as when the allowance is used up, leaves the result for a later run. Attempts are capped, so an
      exhausted allowance doesn't keep transcribing.
- Planned, not built: agreement between independent sources outweighing a single model's confidence (with a switch to
  send such cases to review instead). The setting is shown disabled ("coming later").
- Confidence is not comparable across models, so thresholds are per service and model (`ConfidenceCalibration`).
  Starting points (floors): Deepgram word confidence 0.90; Whisper-family services (built-in, local, OpenAI), whose words
  carry a probability, e^−0.3 ≈ 0.74, the word-level equivalent of Whisper's average log-probability guard of −0.3. The
  whole-file check drops findings resting on words below the threshold.
- Automatic calibration (`TuneConfidence`, off by default; the page explains it): every subtitle found `InSync` or
  `Corrected` by speech-to-text, with no wording-audit findings and text that decoded cleanly, is compared with its sync
  snippets by the same finder (only lines inside the snippets), and each heard word matched to a line adds to a
  hundred-bin histogram per service and model (`<plugin data>/calibration.json`): matched, and flagged (behind a name,
  number or negation difference, which in a good subtitle is a mishearing). The threshold is the lowest at which at
  most 2 % of the matched words would be flagged, once 1,000 words have been seen; never below the floor (so it only
  ever makes the check stricter) and at most 0.99. Counts are halved above five million, and 50 services and models kept.
  Learning never fails a check. "Missing words" findings aren't counted: subtitles condense speech, so that measures
  the subtitle, not the word confidence.

## Editor

The editor opens a subtitle through its result id, so only files this plugin already knows can be opened.

- **Endpoints:**
  - `GET Subtitles/Editor/{id}` returns the lines with their file position and the file's fingerprint.
  - `PUT Subtitles/Editor/{id}` takes the fingerprint and the lines.
  - `GET Subtitles/Editor/{id}/Clip?start=&length=` returns at most 30 s of WAV (16 kHz mono) from the audio track
    `AudioChoice` picks for the subtitle's language. The page fetches it with the session's authorisation and plays
    it from a blob, so no token appears in a URL.
- **Checks on save:**
  - The file must still have the fingerprint it was loaded with.
  - Times must be finite, start at 0 or later, end after they start, and be at most a day.
  - Text must be non-empty and at most 1,000 characters, and there can be at most 20,000 lines.
- **What is kept:** lines keep their identifiers, settings and ASS fields through their file position, and new lines
  take the first line's style. Lines are sorted by start time.
- **Saving:** the file is written through `SubtitleFiles.Replace`, so the first original is kept for Undo. The number
  of lines changed is added to `Cleaned` as `EditedByHand`.

## Encodings

- **Reading:** a file that isn't UTF-8 or UTF-16 (by byte-order mark or strict UTF-8 validation) is decoded with the
  code page for the language tag in its name (`SubtitleEncoding.CodePageFor`), or Windows-1252 when there is none.
- **Writing:** the encoding read is kept on the document (`SourceEncoding`). `SubtitleWriter.ToBytes` writes a legacy
  file back in the same code page, refusing characters it can't hold, so unchanged lines round-trip byte for byte even
  when the guess was wrong. UTF-8 and UTF-16 input is written as UTF-8.
- **Suspect text:** replacement or C1 control characters mark the text as suspect (`TextSuspect`). Then timing and
  text changes wait for review, only empty lines are removed automatically, and the wording isn't audited.

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

## Libraries

`LibraryScope` decides which videos the plugin works on. The server's film, show and mixed libraries are read from
Jellyfin's virtual folders (id, name, folders); a video belongs to the library whose folder holds it (the deepest one,
when folders nest), so the decision is a plain path match with no extra database queries in the nightly walk. The
setting is `ExcludedLibraries` (ids): a library added later is worked on until it is unticked, and an id that no longer
names a library changes nothing. A video in no known library's folder is never left out. `LibraryVideos` applies the
scope to every walk, so the checks, the search, generating and the whole-file check all honour it (a whole-file check
picked for a video in a library left out is cleared as unreachable, with the reason).

## Settings: basic vs advanced

Basic settings are the key decisions in plain language. Advanced settings (costs, per-run limits, clean-up details, AI
checks) sit behind a collapsed section with a warning; risky values show their own warning.

## Languages

Same-language subtitles for any language the providers support. Different audio and subtitle languages are on the
roadmap: identify both languages, transcribe, translate (Whisper can translate straight to English), and synchronise
mainly on the speech/silence pattern, with fuzzy text matching as a secondary signal.
