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
machine-/AI-translated flags; hearing-impaired and forced preferences; language check of the text itself (its writing
system for any language, common words for some; see [Languages](#languages)).

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
  subtitles in the wrong language (nothing matches), which the language-independent stage can't. Only subtitles in
  the audio's language get this stage; see [Languages](#languages).
  Subtitles made for a different cut, whose anchors split into clusters along the timeline, are left for the
  [fix by section](#fix-by-section-different-cuts), which uses a full transcript.

### Speech-to-text tiers

| Tier | Purpose | Notes |
|---|---|---|
| A: sync snippets | Check and synchronise | A few minutes per video at most |
| B: AI context | Excerpt handed to the AI plugin | Extends tier A's snippets to a target length rather than transcribing afresh |
| C: full transcript | Last-resort subtitles, the whole-file check and the fix by section | The whole video in 10-minute chunks; used by [Generated subtitles](#generated-subtitles), the [Whole-file check](#whole-file-check) and the [Fix by section](#fix-by-section-different-cuts) |

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
  (words packed closer than that push the next cue on). Chinese and Japanese words are joined without spaces (Korean
  keeps its spaces), and their lines are shorter (`CueRules.For`; see [Languages](#languages)).
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
  (`JOEY:`, `PHOEBE & JOEY:`) removed; case and punctuation ignored; English contractions split (`don't` → `do not`,
  `can't` → `can not`, `'s` → `is` on both sides), a doubled apostrophe (`don'’t`) read as one and a dropped g
  (`nothin'`) spelled out; numbers in words become digits (`twenty-five` 25, `one hundred and five` 105, `nineteen
  ninety` 1990, `a thousand` 1000; a comma ends a number, so "two, three" stays two), digit separators dropped; times
  alike however written (`6:00` and `6.00` are 6, `9:30` and `9.30` are 9 and 30); a code joined to its number (`XR-7`
  is `XR7`). A "no" on its own (followed by a comma or stop: "No, thank you") is an interjection, not a negation. Names
  are capitalised words where a sentence doesn't start (not "I"; a title's stop, `Mr.`, doesn't end one); not in
  German, or in text all in one case. Heard words the service splits at punctuation (`$40` `,000`, `a` `.m.`, `K`
  `-9`) are joined again first.
- **Anchoring** (`Anchored`, on): a line's words and those heard for it are compared in order (longest common
  subsequence); a stretch of unmatched words is anchored when it has at most two words on each side and matched words
  on both sides, or on one side where words are replaced ("two years" heard as "three years" at the start of a line),
  or, for `not`, a matched word before it ("we don't" heard as "we do"). A repeat of the word next to it isn't anchored.
  A clause the subtitle leaves out, a word said twice ("I haven't, I haven't") or speech-to-text splitting a word
  ("nutso" heard as "not so") then isn't a difference.
- **Findings** (one per line; kinds, most important first):
  - `negation`: the line and what is heard for it have different numbers of `not`/`no`/`never`/`nothing`/`nobody`/
    `none`/`neither`/`nor`/`nowhere`, and so do the line with its neighbours; anchored, only negations that are the one
    word added, dropped or replaced in their stretch are counted.
  - `number`: a number in the line not heard for it or its neighbours, or heard but not in the line or its neighbours
    (a line break in another place isn't a difference); anchored, only a number added or dropped, or in the place of
    another number (a number heard for a word is speech-to-text hearing "a billion" for an invented word), and "one"
    only in the place of a number (alone it is as often a pronoun). With one on each side, the fix replaces it in place.
  - `name`: a word heard in the place of another (in the aligned line) where either is a name, and neither appears on
    the other side nearby; with `KnownNames` (on), one name in the place of one other, the heard name (or one spelled
    like it: the same Soundex, or at most a third of the letters different) being a name the subtitle writes elsewhere
    and not the line's own. Speech-to-text spells invented names its own way ("Kyrell" heard as "Carol"); a line naming the
    wrong character names one the subtitle knows. With one, the fix replaces it in place.
  - `words`: at least 5 different heard words, over 60 % of those heard for the line, aren't in it or its neighbours.
  - `missing-line`: heard speech (split at pauses over 1 s) of at least 4 different words and 1 s that no line is shown
    during; with `Isolated` (on), not when more than two are heard within a minute of each other (a song or a radio the
    subtitle leaves out). The fix adds it, from its first word to its last (at least 1 s, ending before the next line).
  - `extra`: a line of at least 8 spoken words with nothing heard within ±3 s; with `Isolated`, only when the spoken
    lines either side of it were heard (a run of them is speech-to-text missing a noisy stretch). Music (♪, ♫, `#`) and
    sound descriptions are never flagged. The fix removes it.
  - Otherwise the fix is the heard words as a line (a capital first, wrapped at 42 characters).
  - Counting different words (`Distinct`, on) keeps chanting, laughter and repeats ("whoop, whoop, whoop …") from being
    lines or differences.
- **Guards:** with at least 20 subtitle words and fewer than 25 % of them heard, or with lines of 3 or more words with
  nothing heard more than a quarter of the spoken lines (at least 4; whether or not they would be flagged), the
  transcript is taken to be at fault (another version or language, music, the wrong audio track) and nothing is
  flagged; the result says so. At most 50 findings are kept (the counts cover them all).
- **Confidence:** a finding resting on heard words below the service's threshold is dropped (the result says how many):
  the heard number or name, the heard negation, for a negation the line has but wasn't heard the least of the words
  heard in its place and either side ("have" for "haven't"), or the mean of the words behind a missing line or missing
  words. Services that give no confidence (OpenAI's whisper-1) aren't second-guessed. Thresholds: see
  [Decisions and confidence](#decisions-and-confidence).
- **Calibration on real videos:** the thresholds and rules above were set by comparing subtitles with full transcripts
  of their videos from a local Whisper-family service (a small model), with the evaluation tool in `tools/DiscrepancyEval`
  (it takes a list of videos and subtitles, caches transcripts, runs a grid of options and reports findings per kind
  per hour and recall on damaged copies). The sample: 19 subtitles taken to be good (17 episodes the audio check had
  found in sync, 2 films; comedies, dramas, action and animation, from the 1990s to the 2020s), 14.0 hours; 3 of them
  paired with another episode of the same show; and 3 damaged copies (3.1 hours) with 59 known changes: 15 lines
  deleted, 15 numbers changed, 9 negations dropped, 12 names swapped for another character's and 8 long lines added in
  silent gaps.
  - Findings per hour on the good subtitles: **53.7 before, 1.9 after** (negation 14.3 → 0.5, number 6.8 → 0.6, name
    16.9 → 0.1, words 1.3 → 0, missing line 2.1 → 0.1, nothing heard 12.3 → 0.6). Before, most were speech-to-text
    mishearing invented names, subtitles leaving out repeats or clauses, "No"/"Oh" confusions, number formats
    ("$40,000" heard as "$40" ",000", "XR-7" / "XR7", "6:00" / "6 a.m."), chanting and song lyrics, and short lines
    speech-to-text missed. Of the 26 left after, 8 are real faults in the subtitles (digits split by text recognition,
    such as "Level 1 4", and a garbled line), 1 a real difference (a broadcaster's announcement), 3 can't be told
    without listening, and 14 are false: short lines missed in noise or overlapping speech, negations misheard ("had"
    for "hadn't"), a song, a place name — about 1 an hour.
  - Found on the damaged copies (same kind at the same place): 44 of 59 before, 43 after (negations 7 of 9 both; numbers
    14 → 13 of 15; names 6 → 3 of 12, as speech-to-text rarely spells an invented name the subtitle's way; deleted
    lines 9 → 13 of 15, from the shorter 1 s minimum; added lines 8 → 7 of 8).
  - Each wrong-episode pairing tripped the shared-words guard (under 10 % heard) before and after.
  - Tried and not taken: a tolerance of 2 or 4 s (the same results as 3 s), a confidence floor of 0.85 or 0.90 (fewer
    numbers found), lines with nothing heard at 4–6 words (three to eight times as many false findings) or 10 (added lines
    missed), missing lines of 6 words over 2.5 s (most deleted lines missed).
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

## Fix by section (different cuts)

A subtitle made for another cut of the video (a scene added or removed, a recap or cold open, ad breaks trimmed, often
with a frame-rate change too) starts in time and then jumps or drifts part-way, so no single offset fits: the regular
check leaves it unclear, or settles it on the snippets that happen to agree. The fix by section fits it to a full
transcript piece by piece and proposes the correction for review; it never applies it on its own.

- **Switch:** `FixDifferentCuts` (off by default, `SectionFixer.OnByDefault`); **Try fixing timing by section** in the
  results (`POST Subtitles/Results/{id}/FixBySection`) asks for any candidate whatever the switch. With a full transcript
  already kept (by the whole-file check or generating, same service and model) the fit is made during the request (a few
  milliseconds); otherwise the file is queued (`SectionFixRequested`) for the next full-transcript run. A file the run
  couldn't fix is refused with 400 and the reason (`SectionFixer.Ineligible`: the whole-file check's rules, and a
  candidate timing). Needs the Full transcript tier; the page switches it on with the switch.
- **Which files** (`SectionFixer.IsCandidate`, `Choose`): results `Unreliable` ("Unclear"), or settled by speech-to-text
  (`InSync`, `Corrected`, `Proposed`) with a confidence under 0.75 (`PartialAgreement`, the share of matched words
  agreeing: snippets can agree while the timing jumps between them); not matched by meaning, generated, embedded, added
  by the search (chosen among candidates for fitting), with text that didn't decode cleanly, declined, undone, or fixed
  by section already; in the audio's language. Asked-for first, then oldest; not tried again once tried, except a failed
  transcript after 3 days. `WrongLanguage` results are left alone.
- **Where it runs:** a step of the full-transcript task (`ShoalSubtitlesGenerate`), after files asked for (whole-file
  checks, then fixes) and generation, and before the automatic whole-file checks, so a check that follows compares the
  lines at their proposed times (`WholeFileChecker.Clocks` maps each section). It shares the task's time budget
  (`MaxGenerateHours`) and the per-night limit `MaxWholeFileChecksPerNight` (5), counting every file tried; purpose
  `subtitles.sections` for metering. Transcripts come from `TranscriptCache` (shared with generating and the whole-file
  check), so a fix after either costs nothing more.
- **Anchors:** as for sync snippets, every run of three words that occurs exactly once in the subtitle and once in the
  whole transcript (`TranscriptAligner.Anchors`), sorted by subtitle time. At least 20, or the fit is rejected.
- **Segmentation** (`PiecewiseAligner`, pure and deterministic): for each frame-rate ratio of the snippet solver, the
  anchors' offsets (audio time − ratio × subtitle time) are grouped into at most 16 candidate offsets (the densest ±0.5 s
  cluster, then the densest of the rest, each of at least 8 anchors). Dynamic programming (Viterbi) assigns the anchors,
  in subtitle order, to candidates: an anchor more than 0.5 s from its candidate costs 1, a change of candidate costs 16.
  Runs that are too thin (under 8 agreeing anchors) or too short (under 20 s of subtitle time from the first to the last)
  are folded into the neighbour they agree with more, the weakest first; neighbours less than 1 s apart are one section.
  Each ratio's cost is the disagreeing anchors plus 16 per jump. The lowest cost wins; the simpler ratio (same rate first) is kept unless another costs
  more than 1 less. Each section's offset is the median over its agreeing anchors at line starts (at least 5), else all.
- **Rejection** (left alone, with the reason noted): fewer than 20 anchors; no section; more than 8 sections; fewer than
  half of all anchors agreeing with their section; or fewer than half of a section's own anchors agreeing. Another
  episode, another language or notes that aren't dialogue give few anchors, and those don't line up.
- **Jumps between lines:** for each pair of neighbouring sections, the lines between the last anchor of the first and the
  first anchor of the second are scored under each section's timing: the share of each line's different words heard in
  the transcript within 1.5 s of where the timing puts it. The jump goes where the lines before it match the first
  section and the lines after it the second; ties go to the longer pause. When the offset falls by 5 s or more (the
  subtitle has a part the video doesn't), a run of lines between them may be flagged instead: it must make room, so that
  the lines kept either side don't overlap after moving (0.5 s allowed); keeping a spoken line there costs 0.25 unless it
  is heard, and flagging a heard line costs what it matches. A smaller fall (a trimmed pause at an ad break) flags
  nothing: the lines either side may overlap a little, which the clean-up tidies. A section starts (`TimingSection.From`)
  midway in the pause before its first line (or at that line's start when lines overlap), and each line moves with the
  section its start is in, so a line is never split. Lines that would move before the video starts or past its end are
  flagged too (a recap or cold open the video doesn't have).
- **Results:** a fit with jumps becomes `Proposed` with `Scale`, `Offset` (the first section's) and `Sections` (from,
  offset, anchors, where it shows in the video), `Stage` `timing by section`, and `Confidence` the share of anchors
  agreeing. Flagged lines are `LineFinding`s of kind `not-in-video` with `From` = `timing by section`, applied (the line
  removed) or declined one at a time in review. One timing for the whole file is proposed the same way only for a
  frame-rate change, flagged lines or a shift of at least 0.5 s (`OneTimingWithin`; good subtitles sit a few tenths
  early); otherwise, and when rejected, the result keeps its status and notes why. `SectionFix` records when, the service
  and a summary; the explanation ends "Timing by section: Timing jumps at 12:40 (+3.2 s) and 31:05 (−41.0 s): subtitle made
  for a different cut. Sections: …", and the nerd stats list the sections.
- **Apply / Decline / Undo:** Apply removes the lines still flagged (not declined), then moves each line by its section
  (`PiecewiseFit.Retime`), then the clean-up as usual; counted as `RemovedNotInVideo`. The first original is kept, also
  for a file an earlier correction changed, so Undo brings back the file as it was before the plugin touched it. Decline
  clears the proposal and the flagged lines, and the file isn't proposed again unless it changes.
- **Calibration on real videos** (`tools/DiscrepancyEval`, command `sections`, with the whole-file check's cached
  transcripts from a local Whisper-family service, a small model): 27 subtitles taken to be good (22 episodes, 3 films,
  two alternative subtitle releases; 18.4 hours of audio, about 5 of them transcribed for this), cut synthetically in
  312 ways (24 of them × 3 each: a 30–120 s stretch removed from the subtitle, a 30–120 s block from another subtitle
  inserted, both at once, a stretch removed from a subtitle retimed for 25 fps; and a 30–90 s block before the start),
  19 subtitles paired with another episode of the same show, and 698 pairings of every good subtitle with every other
  video.
  - Cuts: the right number of sections and frame-rate ratio in 312 of 312; section offsets within 0.023 s of the truth
    (median), 0.27 s at worst; jumps placed at the right line in the video within 0.04 s (median; 255 of 288 within 1 s,
    the rest an unheard interjection next to the cut placed on the wrong side); 99.97 % of lines moved to within 1 s of
    where they belong. Lines of a part the video doesn't have: 99.6 % flagged, 99.3 % of flagged lines were such lines.
  - Untouched subtitles: one timing for 25 of 27. The other two, both episodes of one show whose regular check had only
    partly agreed (confidence 0.6 and 0.7), really do jump: their offsets are flat within stretches and step by about
    1–1.4 s at the act breaks (broadcast breaks trimmed differently), which the fit found.
  - Another episode: 19 of 19 rejected (at most 181 anchors, none agreeing for long enough); cross pairings: 698 of 698
    rejected.
  - Tried: a jump penalty of 4 (a false jump on a good subtitle with 20 s sections), 8 and 16 (none); a smallest jump of
    0.8 s (an extra false jump, and 4 of the 312 cuts split in two), 1.0 and 1.25 s (the same), 1.5 s (real 1.2–1.4 s
    act-break steps merged, fitted as a false 0.1 % frame-rate change instead); sections of at least 20, 30 or 60 s (30 s
    folded a real 23 s closing section into the one before); an extra cost for agreeing anchors' distance from their
    section's offset (meant to tell drift from steps: no difference once the smallest jump was 1 s); agreement within
    ±0.4 s (a cut missed) or ±0.7 s (six cuts miscounted, and with 40 % agreement one wrong-episode pairing accepted);
    at least 40 %, 50 % or 60 % of anchors agreeing (60 % lost 6 to 37 cuts); 6, 8 or 12 anchors a section (the same);
    a cost for keeping an unheard line next to a missing part of 0, 0.1, 0.25 or 0.4 (lines of the missing part flagged:
    94 %, 99 %, 99.6 %, 99.8 %; flagged lines that were: 99.5 %, 99.5 %, 99.3 %, 99.2 %). Flagging needs a fall of
    at least 5 s: on a real episode, a 1.5 s fall at an ad break flagged a line that was there.
  - The real unclear results in a small library: one episode with three act-break jumps (−1.5, −1.3 and −1.6 s) is
    proposed; a storyboard-notes track, and two short clips with almost no speech, are rejected.

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
  carry a probability, 0.80 (it was e^−0.3 ≈ 0.74, the word-level equivalent of Whisper's average log-probability guard
  of −0.3, until the [calibration](#whole-file-check) showed 0.80 cuts the findings on good subtitles from 2.7 to
  1.9 an hour, mostly misheard negations and numbers, without missing more of the known changes). The whole-file
  check drops findings resting on words below the threshold.
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

**Restore all originals** (`SubtitleProcessor.RestoreAll`, for before uninstalling) applies Undo's own rules to every
result that holds a change: results with the original kept (`Changed` and a `Backup`) get it back, then subtitles the
plugin added or generated (`Added`/`Generated` and `Changed`) are removed. Originals go first so a subtitle that was
added and later corrected is back as added, and so still removable. A file whose content isn't what the plugin last
wrote (changed since, including an added file someone edited) is left alone, as is one whose original is no longer kept,
and a changed file that has since been deleted isn't recreated; each is reported with the reason. An added file that is
already gone is simply recorded as undone. Restored results are `Undone`, which later runs leave alone. The endpoint
works in batches of 200 in a fixed order (originals, then removals, each by result id) and returns a cursor, so the
page shows progress without a background job, and a batch never runs alongside a scheduled task or new-video run
(`RunGate`). Jellyfin is told about each file restored or removed.

## Speech-to-text failures

- **Retries** (`SpeechRetry`, `RetryingSpeechToText`): remote services (Deepgram, OpenAI, the local service) try a
  failed call again for `Transient` and `NoConnection` failures: 5 attempts in all, waits from common's
  `BackoffSchedule` (transient: 2 s doubling, half to all of it by jitter), each at most 60 s; a `Retry-After` is
  honoured up to 60 s, and a longer one (a 429 asking for more is a `ProviderLimit` anyway) isn't waited out. No retry
  starts once 5 minutes have passed since the first attempt, so a whole-video chunk that timed out isn't resent.
  `Authentication`, `BadRequest` and `ProviderLimit` are never retried. The retrying wrapper sits inside
  `MeteredSpeechToText`, so one reservation covers every attempt. The built-in service is wrapped with one attempt, only
  to count its calls.
- **Fallback chain** (`SpeechFallback.Chain`, `FallbackSpeechToText`), for checks (snippets and AI context; not full
  transcripts, whose cache is keyed by service): the chosen service, then the local service (address set, not the one
  that failed), then built-in (allowed, a model already installed: a fallback never starts a download, not the one that
  failed). Never a paid service. Anything but `BadRequest` falls back. A failed service is passed over for 10 minutes,
  or for the run when it refused the key, is over a limit or is `ServiceBroken` (the built-in program can't start).
  Transcripts from a stand-in carry `FallbackFrom`/`FallbackReason`; the result records `SpeechFallback` (from, to,
  reason), and its `SpeechSetup` is the stand-in's, so an unclear result is checked again with the chosen service.
  Switch: `FallBackToFree` (on).
- **Deferred**: when the line-start stage couldn't decide and speech-to-text failed (not `BadRequest`), no verdict is
  recorded: `ResultStatus.Deferred`, nothing changed, checked again on the next run (after new files, so deferred ones
  never crowd them out); the search (`find-`) and embedded tracks likewise. A decided line-start verdict stands, with
  the failure noted.
- **Waiting for speech-to-text** (`SpeechReadiness`): while no service can be used (none set up; the chosen one and
  every stand-in passed over in the run; or each in a systemic problem whose last failure was under a day ago), a
  deferred check on an unchanged file (`WaitsForSpeech`: same fingerprint, no rerun asked) is skipped without redoing
  the line-start stage, and a deferred search is skipped without downloading ("waiting for speech-to-text"); neither
  counts against the run's files or searches. A systemic problem stops holding them back a day after its last
  failure, so they are tried at least daily. A new search can't know beforehand whether it will need speech-to-text,
  so only searches already deferred wait.
- **Rerun**: `POST Subtitles/Results/{id}/Rerun` sets `RerunWith` to the service first chosen, for a subtitle file's own
  check (plain id) that fell back or failed, and only while that service is usable now (`SpeechFallback.Usable`: key and
  paid use allowed; address; allowed and installed). The next check run takes those first and builds that service
  (`RunStart.SpeechWith`, the tier's model when it names the same service); the new result clears the flag.
- **Built-in contingencies** (`BuiltInSpeechToText`): the program failing to start (`Win32Exception`/`IOException`), or
  crashing twice in a row (SIGILL, SIGABRT, SIGBUS, SIGFPE, SIGSEGV; Windows access violation or illegal instruction),
  has the install verified once (`BuiltInInstaller.VerifyAsync`: checksums and the execute bit). Damaged: the program
  folder is removed, `NeedsRepair` set, and the host's single-flight background download started (the service exists
  only with consent); the page shows "needs repairing" with **Download again**. Intact: the server can't run it. Either
  way the failure is `ServiceBroken`. A normal non-zero exit, or a kill (137, out of memory), fails that audio only.
- **Health** (`SpeechErrorLog`, `SpeechHealth.Classify`): each call is counted per service per hour (48 h kept) and per
  run (last 10), with successes in a row; each call that failed at least once is appended to `speech-errors.jsonl`
  (time, service, class, message of at most 300 characters with keys already removed, recovered, attempts, run; the last
  500 within 30 days, rewritten atomically when trimmed). Systemic: 5 calls failed for good in 24 h; or at least half of
  at least 5 calls; or failures on 3 runs in a row; or an unrecovered `Authentication`/`ProviderLimit` failure with no
  success since. Three successes in a row clear it. Advice per kind: paid (key, network, credit, status page), local
  (not answering at its address: is it running?), built-in (Download again, CPU and memory). A systemic problem goes to
  the page's banner and the Activity log (once a day per service); transitory ones only to the nerd stats and
  **Recent speech errors** (`GET Subtitles/SpeechHealth`, paged).

## Results page

- `ResultPresenter` (pure, tested) turns a result into its row: `Headline`/`Subline` from `VideoIdentity` (the
  controller looks the item up by id, else by video path, cached 30 minutes in `VideoIdentityCache`; else
  `FromFileName` reads `Series - S01E05 - Title`, `Series.1x05`, `Season 1/S01E05`, `Title (Year)`, `Title.Year.1080p`),
  a language tag, status words, one-sentence `Summary`, `Chips` (⏱ timing, ↔ tidied, 🔈 sounds, ✂ removed, 💬 wording,
  ➕ lines to add, 🔤 encoding, ⏳ queued; pending ones separate), `NerdStats` (parsed from the explanation where the
  numbers live) and a `RelativeTime` (just now, minutes, hours within a day, yesterday and days by the server's
  calendar, then "12 Sep").
- `GET Subtitles/Results/Page?offset&limit&filter&q` (`ResultQuery`): waiting for review first, then newest; filters
  `waiting`, `wholefile`, `queued`, `fellback` or a status; search over name and paths; a tally for the filter and
  summary. The page loads 15 and appends with **Show more**; a refresh reloads as many as are shown.
- **Bulk actions** (`BulkJobs`, `BulkRules`, `BulkSelection`). A selection is either result ids (rows ticked) or a
  filter and search (`{ Filter, Q }`, less `Except` ids unticked afterwards), resolved on the server with
  `ResultQuery.Matches` over `Ordered()`, so "Select all matching" means exactly what the list shows, loaded or not.
  `POST Subtitles/Results/Bulk/Preview` counts how many of a selection each action applies to (`BulkRules.WhyNot`,
  the rules the page uses for each row's buttons: Apply needs something applicable waiting, Decline anything waiting,
  Undo a change whose original is kept (or an added/generated subtitle), Check again neither a change nor a review).
  `POST Subtitles/Results/Bulk` keeps only the results the action applies to, in list order, capped at
  `BulkRules.MaxPerJob` (5,000; the rest are counted as `Left`), and answers 202 with the job, or 409 while another runs
  (one at a time; only the latest job is kept, in memory). The job runs on the thread pool: it first takes the
  `RunGate` alone with `EnterAloneAsync` (waiting for the nightly tasks, a new-video run or a restore to finish;
  `WaitingFor` says which), so they in turn wait for it. Each item is read again and checked against `WhyNot`, then
  done through `SubtitleProcessor` exactly as its single-item endpoint (the fingerprint check in `SubtitleFiles` skips a
  file changed since; Undo refuses a file edited after the change). An `InvalidOperationException` is a skip with its
  message, anything else a failure; neither stops the job. Each decision is saved at once, as for single actions.
  `GET Subtitles/Results/Bulk/{id}` gives `Total`, `Done`, `Succeeded`, `Skipped`/`Failed` (`{ Id, Headline, Reason }`),
  `State` (Waiting, Running, Done, Cancelled) and, at the end, a `Summary` line that also goes to the Activity log.
  `DELETE` stops a job after the item in hand; it is a hosted service, so the server stopping cancels it too. The page
  polls every second, then clears the selection and reloads the list.
- **Apply all suggestions / Decline all** for one result's findings (`POST Subtitles/Results/{id}/Findings/Apply`,
  `…/Decline`; `SubtitleProcessor.ApplyFindings`, `DeclineFindings`): every finding with a suggestion is applied as
  `ApplyFinding` would, in one write that keeps the first original; lines with nothing heard, and findings whose line
  changed since, keep waiting. A timing correction or clean-up waiting for review is left as it is.

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

### One budget page (Shoal AI)

The currency, the overall monthly limit and a limit per paid service are set in Shoal AI when it is installed and
allows this plugin (**Allow Subtitles to use this budget for paid speech-to-text**, on by default there). Keys stay here.

- **Metering:** `Spending.Meter` gives `MeteredSpeechToText` (single calls and whole videos alike) common's
  `BridgedSpendMeter`: each call is still priced here, from the audio length and `prices.json`, then reserved and settled
  on Shoal AI's ledger through its spending entry point (common's `SpendingBridgeClient`, version 1), which converts it
  with its exchange rates and checks its overall limit and the service's own limit. Deepgram is `deepgram` there;
  OpenAI speech-to-text is `openai-speech` (apart from OpenAI's text models).
- **Falling back:** if Shoal AI isn't installed, speaks another contract version, or doesn't allow this plugin
  (`not-installed`, `unsupported-version`, `not-allowed`), the call is metered on this plugin's own ledger with its own
  currency and limit, exactly as before. A refusal by Shoal AI's limits stops the call; so does Shoal AI not answering
  (`transient`), since its limits wouldn't see spending here. Each reservation is settled in the ledger that made it: a
  call is counted once. While Shoal AI is installed, paid services count as usable whatever this plugin's own limit is
  (`Spending.PaidMayBeUsed`); the limit that applies decides at each call.
- **This month's earlier spending:** before its first reservation there (and when the settings page loads),
  `SpendCarry` reports this plugin's own spending this month to Shoal AI as one total per service and currency, which
  replaces what it reported before, so the month counts it once however often it is sent. The simpler alternative,
  showing both until the month ends, would let the two together overshoot the limit. The own ledger keeps its entries
  for when it has to fall back.
- **Settings page:** `GET Subtitles/Spending` returns Shoal AI's summary with `SetInAi` (currency, overall limit, this
  month's spending of all its paid services, per service with `ProviderLimits`, the rates behind them). The page then
  shows "Spending limits are set in Shoal AI" with a link to its page and hides the currency, monthly limit, "No
  spending limit" and the taxes-and-fees percentage (still saved as they were). Shoal AI installed but not answering
  still counts as its budget (`Problem` says why the figures are missing); otherwise the page shows its own settings.
- **Interrupted calls:** a reservation on Shoal AI's ledger left open for an hour (the server stopped mid-call) is
  settled at its estimate there.

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
  tier's service transcribes it, and a paid service is wrapped in `MeteredSpeechToText` under the caller's purpose
  (metered on Shoal AI's budget when it keeps it, as above). A
  semaphore lets only one transcription run at a time, so the built-in service never runs twice at once.

## New videos

`NewItemsHost` (a hosted service) listens to `ILibraryManager.ItemAdded` and `ItemUpdated` for films and episodes (not
for artwork-only updates) and queues them in `NewItemsWaiting`: one entry per video, "added" winning over "changed",
at most 500 (the rest are left to the nightly tasks). Each report re-arms one timer for the quiet delay
(`NewItemsDelayMinutes`, 10 by default), so the batch runs once nothing has arrived for that long. The handler itself is
cheap and never throws; it runs on Jellyfin's scanning thread.

A batch runs the nightly tasks' own code (`SubtitleSyncTask.RunForAsync`, then `SubtitleFindTask.RunForAsync`), made
through dependency injection, with the walk limited to the batch's videos. So every rule applies unchanged: the setup
gate, the results file being readable, the library picker, languages, files and videos per run, downloads per day
(`DownloadLedger`) and spending (`SpendLedger`). Differences, on purpose:

- A video that only changed (typically a new subtitle file beside it) has only files the results don't know checked,
  and isn't searched for: Jellyfin reports videos as changed for many reasons, and the nightly run sees to the rest.
- No pruning of results and no audit of earlier results (both whole-library chores).
- The day's new-video runs share one run's allowance of AI checks (`DailyAiChecks`), so frequent small runs never ask
  more than one nightly run may.
- Generating stays nightly (hours of CPU). A new video the search found nothing for gets its "not found" result like
  any other and is a candidate for the next night's generation.

`RunGate` keeps this apart from the nightly tasks: they share the gate; a new-video run (and "Restore all originals")
needs it alone. A scheduled task that starts during a new-video run waits for it; a new-video batch that finds a
scheduled task running is put back and tried again after another quiet delay. The queue is in memory: after a restart,
the nightly tasks pick up whatever was waiting.

## Libraries

`LibraryScope` decides which videos the plugin works on. The server's film, show and mixed libraries are read from
Jellyfin's virtual folders (id, name, folders); a video belongs to the library whose folder holds it (the deepest one,
when folders nest), so the decision is a plain path match with no extra database queries in the nightly walk. The
setting is `ExcludedLibraries` (ids): a library added later is worked on until it is unticked, and an id that no longer
names a library changes nothing. A video in no known library's folder is never left out. `LibraryVideos` applies the
scope to every walk, so the checks, the search, generating and the whole-file check all honour it (a whole-file check
picked for a video in a library left out is cleared as unreachable, with the reason).

## Settings: basic vs advanced

Basic settings are the key decisions in plain language, grouped in sections that open and close (General; Finding,
checking and generating; and the Spending limit start open; Clean-up and Speech-to-text start closed, and a section
opens itself when it holds a permission still to give). Advanced settings (costs, per-run limits, clean-up details, AI
checks, recent speech errors) sit behind a collapsed section with a warning; risky values show their own warning.

## Languages

Which languages a video's subtitles are wanted in (`LanguageSettings.Choose`, per library through `LibraryScope`):

1. The plugin's **Subtitle languages**, when it names any recognised language (an entry that isn't one is ignored and
   logged). It applies to every library.
2. Otherwise the library's subtitle download languages (`LibraryOptions.SubtitleDownloadLanguages`, set in Jellyfin's
   library settings).
3. Otherwise the server's preferred metadata language: Jellyfin has no server-wide subtitle language (only per-user
   preferences), and the metadata language is the closest the server has to "the household's language".
4. Otherwise English.

The walk carries each video's languages, so checks, searches, embedded tracks, generation and the whole-file check all
use the video's own library's list. Where no single video is concerned, the union over the libraries worked on is used.
The settings page lists what is in effect per library, and where it came from. A language named twice in different
forms (`fre`, `fra`, `French`) counts once (`Recognised` keeps the first form), so it isn't searched for twice.

### The audio's language (`SpokenLanguage`)

Every stage that compares a subtitle with what is said works only on a subtitle in the audio's language:

- **The audio's language** (`SpokenLanguage.Heard`): the chosen audio stream's tag (`AudioChoice.For`: the first stream
  in the subtitle's language, else the default, else the first) when it names a language; a missing, `und`, `unk`,
  `mis`, `zxx` or unrecognised tag means the library's first wanted language (plugin setting, library, server, English).
  The walk sets `LibraryLanguage` on every `SubtitleJob` and `EmbeddedJob` (and the controller's `JobFor` does too).
- **Same language** (`Matches`: both known and equal) is required to search for a missing subtitle
  (`FindRules.AudioIsIn` in `LibraryVideos.Missing`), to generate one (`SubtitleGenerator.AudioMatches`), for the
  whole-file check and the fix by section (`WholeFileChecker.SameLanguage`), and to copy out an embedded track
  (`LibraryVideos.EmbeddedTracks` skips one whose language `Differs`).
- **Another language** (`Differs`: both known and different): a subtitle file beside the video in a wanted language
  that isn't the audio's is never lined up by words (`SyncCheck.RunOtherLanguageAsync`): speech-to-text would be told the
  wrong language (or translate), nothing heard could match, and matching by meaning isn't reliable enough to move a
  translation. See [Subtitles in another language than the audio](#subtitles-in-another-language-than-the-audio).
- **Speech-to-text's language** (`SubtitleJob.SpeechLanguage`, `FindJob.SpeechLanguage`, `EmbeddedJob.SpeechLanguage`):
  the audio's language, or the subtitle's when the audio's isn't known (only reached for same-language subtitles). The
  same code goes to the wording audit, the whole-file check's AI confirmation and the line matcher
  (`subtitleLanguage`), whose instructions say the lines and the speech are in that language and ask for reasons in
  English.

### Per stage

- **Finding.** Jellyfin's `ISubtitleManager.SearchSubtitles` takes the three-letter code in either ISO 639-2 form
  (Jellyfin's `FindLanguageInfo` knows both). SubDL gets its own codes (`SubDlSource.LanguageCodes`: two-letter in
  capitals, `NO` for any Norwegian, `PT,BR_PT` for Portuguese, `ZH,ZH_BG` for Chinese). Added files are named with the
  two-letter code (`Film.fr.srt`), or the three-letter one for a language without one.
- **Scoring the text** (`ContentChecks`, `LanguageGuesser`). Codes are compared in their two-letter form, so `fra`,
  `fre` and `French` agree (before, a wanted `fra` or `deu` was never recognised or rejected). First the writing system:
  a language with one clear script (`ScriptFor`: Cyrillic, Greek, Arabic, Hebrew, CJK, Hangul, Thai, Devanagari, or
  Latin for the languages known to use it; none for Serbian) rejects text whose letters are at least 80 % in another
  script (of at least 200 letters). Then common words, for English, French, Spanish, German, Italian, Portuguese, Dutch,
  Swedish, Danish, Norwegian, Polish, Finnish and Turkish: a clear winner (15 % of words, 1.5× the best language outside
  its family) that isn't the wanted language rejects the subtitle, but only when the wanted language's words are known;
  Swedish, Danish and Norwegian form one family and aren't told apart.
- **Timing by speech starts** is language-independent. **Timing by words** (`TranscriptAligner.Anchors`) matches
  scripts written without spaces (Chinese characters, kana, Thai, Lao, Khmer, Myanmar) character by character on both
  sides: a heard word is split into characters timed by their place in it. The word-time lag (0.28 s for whisper.cpp)
  was calibrated on English; other languages use the same value until there is field data.
- **Wording audit and whole-file check.** `SpokenText` splits English contractions and reads English number words
  only for English (or an unknown language). Negations are counted per language (`NegationsFor`: English, French without
  "ne", which speech drops, Spanish, Italian, Portuguese, German, Dutch, Swedish, Danish, Norwegian, Polish); a
  language without a list has no negation rule, so its lines are never flagged for one; a lone "no"/"non"/"não"/"nie"
  followed by a comma or stop is an answer, not a negation. Where number words aren't read (`ReadsNumberWords`), only
  a number heard in the place of another number is a difference ("veinticinco" written for "25" heard isn't). Names
  aren't told by capitals in German. The whole-file check skips languages written without spaces
  (`SpokenLanguage.WrittenWithoutSpaces`; `WholeFileChecker.NoSpaces`), since it compares words.
- **Generating.** The full transcript is asked for the audio's language (which is the subtitle's), cached under it, and
  the file named `<video>.<two-letter>.generated.srt`. `CueRules.For` makes Chinese and Japanese lines 16 characters
  (9 and 7 characters a second) and Korean 20 (12 a second); lines are wrapped between two Chinese or Japanese
  characters, and Korean words keep their spaces.
- **Clean-up.** Speaker labels in any alphabet (`\p{Lu}`), full-width brackets (`（…）`, `【…】`, `［…］`) as sound
  descriptions, and credit lines in French, Spanish, German, Italian, Portuguese, Dutch, the Scandinavian languages and
  Polish besides English (near the start or end only, as before). Site adverts are language-independent.
- **Counting.** The download ledger and **Subtitle downloads per day** are shared by all languages; **Searches per run**
  (`MaxFindsPerRun`) and the nightly generation limit count one per video and language. The search's log line counts
  searches per language (`FindRules.PerLanguage`). The results tally has per-language counts (`ResultTally.Languages`:
  results, searches that found nothing, subtitles left for their language), shown in the page's summary line ("still
  missing: French 3, English 1") and as a `lang:FR` filter (also for bulk actions).

## Subtitles in another language than the audio

Part of the roadmap's "subtitles in a different language from the audio (translation)". What is there now, and the
plan.

### Now

- **Left alone by default.** `SubtitleProcessor.ProcessAsync` sends a subtitle whose language `Differs` from the
  audio's to `SyncCheck.RunOtherLanguageAsync`, which reads no audio and returns `Unreliable` with the stage
  `not checked (other language)`; the result is `OtherLanguage` ("Not the audio's language": "In another language than
  the audio, so its timing was left alone"), with only the harmless clean-up (as for `WrongLanguage`), no wording
  audit and nothing learnt for calibration. It isn't checked again unless the file changes, the audio turns out to be
  in its language, or the experimental setting is switched on (`SubtitleProcessor.NeedsCheck(SubtitleJob, …)`). A
  subtitle checked by an earlier version while its languages weren't told apart (`Unreliable` or `WrongLanguage`) is
  checked once more.
- **Experimental: speech starts alone** (`TimeOtherLanguages`, off; Advanced → Checking). The existing speech-start
  stage (`Synchroniser`) runs alone, with its usual gates (margin 0.03 and 6 standard deviations, offsets under 0.5 s
  at the same frame rate left alone), stage `speech starts (other language)`. In sync: `InSync`. A correction is
  always `Proposed` (waits for review, whatever the timing policy). Unclear: `OtherLanguage`. Speech-to-text is never
  called. Its calibration (48/48 wrong pairings rejected) was on same-language subtitles; a translation's lines start
  where the speech does, so the signal is the same, but the lines are split differently, so it is marked experimental
  until there is field data.
- **Not searched for, not generated.** A missing subtitle in a language the audio isn't in isn't searched for (there is
  nothing to check a download against yet), and embedded tracks in another language aren't copied out.

### Plan

1. **VAD-first timing.** Speech starts (the current stage) first, then a speech/silence profile compared with the
   cues' coverage, per stretch, so a translation's timing can be settled and, with the fix by section's segmentation,
   cut by cut. Field data from the experimental setting decides whether its corrections can be applied without review.
2. **Whisper's translate-to-English as a shortcut.** For English subtitles on audio in another language, whisper.cpp
   and OpenAI-compatible services can translate speech straight to English (`--translate` / `/audio/translations`),
   with segment times. Its words won't match a human translation exactly, so the anchors come from the line matcher
   (by meaning, `MeaningAligner`) rather than exact word runs, gated as matches by meaning are now (always reviewed).
   Needs a `translate` flag on `ISpeechToText` and a separate cache key.
3. **MT/LLM translation.** For other pairs: transcribe in the audio's language, translate the phrases with the AI
   plugin (or a translation service) into the subtitle's language, then match by meaning as in 2. The same step can
   generate a translated subtitle where none is found (a `*.<lang>.generated.srt` labelled as translated), within the
   AI plugin's budget.
4. **Searching for translations.** Once timing can be checked, `LibraryVideos.Missing` also yields languages the audio
   isn't in (behind a setting), each candidate checked by 1 (and 2 or 3 where available) and added only with a
   confident fit.
