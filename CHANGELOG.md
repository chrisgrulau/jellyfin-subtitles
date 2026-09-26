# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow Jellyfin's plugin versioning.

## [Unreleased]

## [0.12.0-alpha] - 2026-09-26

### Changed

- Uses common's follow-ups: a 429 counts as a limit through the shared `RateLimited` flag (still stopping that provider
  for the rest of the run), and SubDL, Deepgram and speech-to-text failures quote the provider's own reply ("SubDL said:
  …") instead of starting with its host name.

- **SUB-25:** the built-in speech-to-text downloads in the background. **Download now** (shown once the download is
  allowed and it isn't there yet) or **Test** starts it and returns at once; the settings page shows a progress bar and
  percentage while it runs, and tests again when it's done. Before, the first Test downloaded 90–200 MB inside the
  request, which a reverse proxy's timeout cancelled every time. Only one download runs at a time; a nightly run that
  needs the built-in speech-to-text still downloads it itself (or waits for the one running). A download is cancelled
  when the server stops. New endpoints: `GET` and `POST Subtitles/BuiltIn/Download`.
- **SUB-29:** **Test** uses the settings page's values as they are, before Save: besides the service, model, address and
  download permission, now also the currency, monthly spending limit, "no limit" and extra charges. They're made safe by
  the same rules Save applies, and nothing is saved. Before, a limit just raised from 0 refused the test until Save.
- **SUB-24:** on Linux, a rewritten subtitle, and one put back by Undo, also keeps the original's group (read with
  `statx`, set with `chown` on the temporary file before it takes the original's place), so a library shared through a
  group stays writable for the other tools and people in it. Where that isn't allowed (Jellyfin's user isn't in the
  group) or the system can't do it, the file gets Jellyfin's group as before, and the rewrite goes ahead; the reason is
  logged at debug level. The owner still becomes Jellyfin's user. macOS keeps the mode only.

## [0.11.0-alpha] - 2026-09-26

### Changed

- **FAM-06:** uses the shared building blocks from the common library (updated to its FAM-06 release).
  - Paid speech-to-text calls are metered by the shared metered call: reserved first, settled at the actual cost, released
    when the provider failed or the call was cancelled. A call that fails in an unexpected way is now recorded at its
    estimate (before, its reservation stayed open, which counted the same).
  - `results.json` and the download count are read and written through the shared JSON file helper, each keeping its
    policy: a damaged results file is set aside and results start afresh, one that can't be read is never overwritten
    (SUB-18); a damaged or unreadable download count starts from zero. Writes are now flushed to disk before the rename.
  - Spending uses the shared spending store. Exchange rates refresh themselves when due: while they are missing or stale,
    the European Central Bank is asked at most every 30 minutes, not at the start of every run and test. The currency
    setting is read with the shared "setting, or USD" rule.
  - The settings page offers the currencies the server sends with the spending summary (`Currencies`), instead of its
    own copy of the list.
  - ffmpeg (reading audio, copying subtitle tracks out of videos) and the built-in speech-to-text run through one shared
    runner, which handles standard error the same way for all three: only its end is kept in memory while the program
    runs, and a failure reports its last 300 characters (before, ffmpeg's messages were read whole and cut to their first
    300 or 4,000 characters).
- **SUB-31:** the code is reorganised without changing what it does, except as noted:
  - Choosing the speech-to-text service moved out of the sync task into its own class; the change policies are read
    from the settings; one helper starts every run (ffmpeg, HTTP client, exchange rates, speech-to-text service).
  - The library is walked by one class for the sync, embedded and find tasks, with one rule for "already has a subtitle
    in this language". An embedded track is now checked when the only subtitle file beside the video in its language is
    forced-only, or picture-based while **Count picture-based subtitles** is off; before, any subtitle file there skipped
    it.
  - Swapping the Deepgram key for a limited one lives with the other Deepgram account calls.
  - Result ids are worked out by the results store itself (the same ids as before, so stored results and their undo
    records carry over).
  - The entry point other plugins call for transcripts forwards to a registered service.

### Fixed

- **SUB-26:** SubDL searches and downloads, Deepgram account calls (balance, key check, creating a limited key) and the
  cloud speech-to-text calls go through the shared provider HTTP helper: every failure is classified, the provider's
  requested wait is read, keys are removed from messages, and no reply or error body is read beyond its size limit
  (before, a large Deepgram error was read whole). A SubDL "too many requests" or used-up allowance now leaves SubDL out
  for the rest of the run instead of asking it again for every remaining video; a rejected Deepgram key is reported as
  an authentication failure rather than a passing one. The missing-subtitle search stops on a classified provider limit
  or sign-in failure; the OpenSubtitles plugin's own limit is recognised where Jellyfin's providers are called.

## [0.10.0-alpha] - 2026-09-26

### Changed

- **SUB-30:** `results.json` is no longer rewritten whole for every result. Plain results are written in batches (every
  25 results or 5 seconds, and when a run ends or the server stops); a result that records a change to a file or its
  undo, a review decision or an added subtitle is still written at once. Results are indexed by id and by path, so large
  libraries don't search the whole list for each file.
- **FAM-08:** settings that did nothing are shown disabled as "coming later" instead of looking active: the **Full
  transcript** use, **Tune confidence thresholds automatically** and **Let agreement between sources settle
  disagreements**. Saving the page no longer changes them. The README and design notes say they're planned. Unused code
  was removed (`SpeechToTextException.RetryAfter` and `NeedsAttention`, `MeteredSpeechToText.Refused`,
  `SpendingLimit.NeedsBuiltInConsent`).

### Fixed

- **SUB-22:** the built-in speech-to-text is no longer downloaded on servers that can't run it. On musl systems
  (Alpine-based images) and with glibc older than 2.35, the settings page, Test and the nightly run say so plainly and
  suggest a local or cloud service instead; nothing is downloaded.
- **SUB-23:** **Find a local service** works when Jellyfin runs in a container (Docker or Podman). It then also looks
  for the suggested service by name and on the host, and suggests running it on a Docker network shared with Jellyfin
  (address `http://speaches:8000/v1`), or on the host with `--add-host=host.docker.internal:host-gateway`, instead of a
  port published only on the host's loopback, which a container can't reach.
- **SUB-24:** a rewritten subtitle, and one put back by Undo, keeps the original's permissions: on Linux and macOS its
  mode (so group write access for other tools survives), on Windows its access list (the file is swapped in with
  `File.Replace`). The owner and group still become Jellyfin's user where the file is written.
- **SUB-25:** a built-in speech-to-text download that stops arriving (an expired NAT entry, flaky Wi-Fi) gives up after
  60 seconds without data and is tried again later. Before, it could wait until the server restarted, and the nightly
  tasks and Ingest's transcript requests queued behind it.
- **SUB-27:** after **Create a transcription-only key**, the settings page shows which key now reads the Deepgram
  balance, as the server set it. Before, the page kept its old choice and the next Save wrote it back, so reading the
  balance failed with the limited key. The page and the result now also say the new key is created in your Deepgram
  project and stays there if the plugin is removed.
- **SUB-29:**
  - The message asking for permission to download the built-in speech-to-text named the box "above"; it's below the
    services, and the message now names it.
  - **Test** uses the permission box as currently ticked, before Save.
  - After **Check now** or **Find missing now**, results refresh every 10 seconds while the task runs, with its
    progress, instead of once after 15 seconds.
- **SUB-19:** a video is no longer recorded as "Nothing fitting found" (and left for 30 days) when no subtitle provider
  answered: none installed in Jellyfin, or every provider failing (for example SubDL down). It's searched again on the
  next run; with no provider installed at all, the run stops early and says so in the log. (Failures inside Jellyfin's
  own providers are handled by Jellyfin and still look like an empty answer.)
- **FAM-07 (settings page):**
  - **Copy commands** works over plain HTTP, where the browser has no clipboard: the commands are shown selected in a
    text box, ready to copy by hand. Before, the button did nothing.
  - Saving a key shows the server's actual reason when it fails (for example that the data folder can't be written),
    not always "That doesn't look like an API key."
  - Status messages (test results, key and task messages, the local-service search) are announced to screen readers.
  - The results and editor tables scroll inside their own box on narrow screens, so the page doesn't scroll sideways.

## [0.9.0-alpha] - 2026-09-26

### Fixed

- **FAM-01:** languages are recognised from a built-in ISO 639 table instead of the server's culture data. Before, on
  servers without ICU or with minimal ICU data (Alpine, some containers), no language was recognised, so nothing was
  checked or found, and nothing said so.
  - **Subtitle languages** now accepts codes in any form or names (`eng`, `fr`, `German`). A three-letter code is kept
    as written.
  - An entry that isn't a language is ignored and noted in the log.

## [0.8.0-alpha] - 2026-09-26

### Added

- **FAM-05: Jellyfin's Activity log.** These are also written under Dashboard → Activity, at most once a day each, so
  they're seen without opening the plugin page:
  - subtitles waiting for review;
  - subtitles added;
  - folders Jellyfin can't write;
  - searches a subtitle provider stopped (not signed in, daily allowance used up).

  **Also write to Jellyfin's Activity log** is on by default.
- **SUB-20: review in the results list.**
  - Items waiting for review are listed first, however old.
  - A filter shows everything, only what's waiting for review, or one status, and a box finds a video by name.
  - **Decline** turns down a proposal. Nothing is changed, and it isn't proposed again unless the file changes.
  - **Check again** checks one subtitle again on the next run. For a missing subtitle it's **Search again**, which
    also works after an added subtitle was undone. A file holding this plugin's changes has to be undone first.
- **SUB-19:**
  - A subtitle the audio check couldn't settle (unclear, or not matching the speech) is checked again once the
    speech-to-text service in use changes, for example after it has been set up.
  - A new install does nothing (no checks, no changes, no downloads) until its settings page has been saved once. An
    install that has already run carries on as before.

## [0.7.0-alpha] - 2026-09-26

### Fixed

- **SUB-15:** the built-in speech-to-text program now installs under Jellyfin's data folder
  (`<data>/shoal-subtitles/builtin`), not under the plugins folder. On Windows its DLLs made Jellyfin list a broken
  "Jellyfin.Plugin.Subtitles" plugin, whose Uninstall button would delete the keys, the results and the undo backups.
  An existing install is moved on first start; if it can't be moved, it is removed and downloaded again, verified,
  when next needed.

### Added

- **Subtitle editor.** **Edit** on a result opens that subtitle's lines on the plugin page.
  - **Editing:** change a line's text, start or end time; delete a line or add one; shift every line at once; find
    lines by text.
  - **Listening:** ▶ plays that line's audio (from a moment before to a moment after), using the audio track that
    suits the subtitle's language.
  - **Saving:** Save writes the file only if it hasn't changed since it was opened. The first original is kept, so
    **Undo** brings it back. It is written in the file's own encoding.
  - **Formats:** styles and identifiers are kept for lines that were already there. Added ASS lines take the first
    line's style.

## [0.6.1-alpha] - 2026-09-26

### Fixed

- **FAM-02:** questions to the AI plugin (lines matched by meaning, the wording audit) carry text in every script as
  it is, not escaped. They are fitted to its limit: shorter texts first, then fewer lines. Before, Cyrillic, Greek,
  Hebrew or Arabic subtitles were six times their size, often refused, and the feature quietly did nothing.
- **FAM-03:** an "off" answer from the AI plugin (switched off, or Subtitles not allowed) is quiet, like a missing
  plugin. Other failures are shown in the result.
- The shared source is updated.

- **SUB-14:** subtitles in legacy encodings are no longer rewritten as garbled text. Before, anything that wasn't
  UTF-8 or UTF-16 was read as Windows-1252 and written back as UTF-8. That garbled Cyrillic, Central European, Greek,
  Turkish, Hebrew, Arabic, Chinese, Japanese and Korean subtitles whenever the timing was corrected.
  - **Writing:** a file is written back in the encoding it was read in, so every line the plugin doesn't change keeps
    its exact bytes, even if the encoding was guessed wrong. UTF-8 and UTF-16 files are written as UTF-8, as before.
  - **Reading:** the encoding is guessed from the language in the file name, for example `Film.ru.srt` as
    Windows-1251, `Film.ja.srt` as Shift-JIS and `Film.zh.srt` as GB18030.
  - **When the text still looks wrong:** if it contains replacement or control characters, nothing is changed on its
    own. Timing and clean-up wait for review, and the result says why.
  - **Unrepresentable edits:** a changed line the file's encoding can't hold is refused, never replaced with "?".
- **SUB-18:** a `results.json` that can't be read, whether locked or its permissions changed, is never overwritten.
  Checks and searches wait until it can be read, because it holds the undo records. A damaged one is set aside as
  `results.json.damaged-…` and results start afresh.
- **SUB-16:** a folder Jellyfin's account can't write is detected before any download or audio work, using a hidden
  test file.
  - Such files are shown as **Can't write here** and tried again after 30 days or when they change. Before, the same
    files were redone every night, using download quota and audio work each time.
  - Failed checks now wait 3 days before being tried again, unless the file changes.
- **SUB-17:** one unexpected error in a file no longer stops the nightly run. It is recorded as a failure with its type,
  and the run carries on. Timecodes and release names only accept ASCII digits, so full-width or Arabic-Indic digits
  are simply not a timecode, instead of throwing.
- **SUB-21:** when Jellyfin finds ffmpeg through the system PATH (portable installs, source builds), Subtitles finds it
  too. Before, both tasks and Ingest's transcript requests stopped with "ffmpeg wasn't found".
- **SUB-28:** a timing decided from lines the AI matched by meaning always waits for review, even with automatic timing
  fixes. The matched lines are shown in the result.

## [0.6.0-alpha] - 2026-09-26

### Added

- **Lines matched by meaning** (optional, with the Shoal AI plugin: **Ask the AI plugin when the wording differs from
  what is said**, on by default, at most 20 checks per run).
  - **When it's used:** speech-to-text heard plenty but the subtitle's words don't match it. Typical cases are a
    translation (an English subtitle on a film in another language), a paraphrase, or dense dialogue.
  - **What happens:** the AI pairs the heard phrases with subtitle lines that say the same thing, and those pairs
    must still agree on one timing before anything is changed.
  - **Where it applies:** finding subtitles, checking existing ones, and checking tracks inside videos. A right but
    loosely worded subtitle is no longer skipped as "another language".
  - **When the content is different:** if the AI says the subtitles are for something else (another version,
    commentary), that confirms the mismatch.
  - **What is sent:** only the subtitle language, a few minutes of heard phrases and the nearby subtitle lines. The
    AI plugin must allow Subtitles, and its spending limits apply.
- The shared source is updated to include the AI plugin's client (`AiBridgeClient`).
- **Wording audit** (optional, with the Shoal AI plugin: **Audit the wording with the AI plugin**, on by default).
  - **When:** after a subtitle's timing is settled by speech-to-text.
  - **What it checks:** the AI compares its lines with what is said during those few minutes. It flags lines whose
    meaning differs, such as a wrong name or number, a missing "not", or missing or extra words. Ordinary subtitle
    shortening isn't flagged.
  - **In the results:** the flagged lines appear under *What changed*, and the *Changes* column shows how many lines
    differ.
  - **Suggested wording:** it waits for review and is never applied on its own. **Apply** uses it (only on lines
    still as they were found), and **Undo** brings the original back.
  - **What is skipped:** subtitles matched by meaning (translations) aren't audited.
  - **Limit:** it shares the run's allowance of AI checks with line matching.
- **Wording audit of earlier subtitles.** After each run's checks, a few subtitles checked before the audit existed
  are audited, oldest first: **Earlier subtitles audited per run**, 5 by default, 0 turns it off.
  - Only subtitles that are in sync or were corrected are audited, and only if the file is unchanged since, nothing
    is waiting for review, and it isn't a translation.
  - A few stretches are transcribed again. If the fresh transcript no longer finds the subtitle in sync, it isn't
    audited, and it isn't tried again until the file changes.
  - Each subtitle is audited once. Each attempt counts towards the per-run number, and audits use what is left of
    the run's AI checks.

### Changed

- **AI checks per run** now counts line matching and wording audits together.

## [0.5.0-alpha] - 2026-09-26

### Added

- **Short transcripts for Ingest** (opt-in: **Let Ingest ask for short transcripts**, off by default). When a new
  video's name doesn't say which episode it is, Ingest can ask for a transcript of up to three minutes of it, which
  the AI plugin compares with the episode synopses.
  - Uses the **Context for AI decisions** speech-to-text service, which must be turned on. Built-in is the default
    and free; a cloud service is metered against this plugin's spending limits like any other call.
  - An in-process entry point (`SpeechBridge.TranscribeAsync`, JSON in and out, no HTTP endpoint), checked like the
    AI plugin's: version, allowed caller, the caller's own purpose, an existing video, and a stretch of at most 180
    seconds. One transcription runs at a time.

## [0.4.0-alpha] - 2026-09-26

### Added

- **Subtitles inside video files** (opt-in: **Also check subtitles inside video files**, off by default).
  - What's checked: text subtitle tracks inside videos (SRT, ASS, WebVTT, MP4 text), in the chosen languages, for
    videos with no subtitle file of that language beside them. Forced-only tracks are skipped.
  - How they're checked: each track is copied out with Jellyfin's ffmpeg, and checked against the audio like any
    other subtitle. Copying reads the video file (all of it for most MKVs), so only a few tracks are checked per run
    (10 by default).
  - What happens after: the video is never changed. A track that's out of time gets a corrected copy added beside the
    video, which Undo removes. A track that's in time is recorded and not read again until the video changes, and a
    failure is tried again after 30 days.
- **SubDL as an extra subtitle source** (the successor to Subscene), searched after Jellyfin's own providers when a free
  SubDL API key is set on the settings page.
  - It searches by the film's or show's IMDb or TMDb id, with season and episode for TV.
  - From a whole-season zip, only the one file for the wanted episode is taken (`S01E02`, `1x02`, or a numbered file
    when the pack uses nothing else), and nothing if that isn't clear.
  - Downloads are capped.
  - SubDL puts the key in its download links, so the links are never logged, shown or stored.
  - A provider that is down is skipped. One whose daily allowance is used up is left out for the rest of the run
    instead of stopping it.
- **Local service helper.** On the settings page, **Find a local service** looks for OpenAI-compatible speech-to-text
  services on this server's usual ports (only this server, never the network). Each one found has a **Use this**
  button. The page also suggests how to run one (speaches in Docker), matched to Jellyfin's hardware acceleration
  setting: the GPU image for NVIDIA, the CPU image otherwise. It shows the commands with a copy button. Nothing is
  installed or started by the plugin.
- **Deepgram credit balance** on the settings page ("Deepgram credit: USD 154.17").
  - Reading it needs a Deepgram Admin or Owner key. You choose whether it's read with a separate billing key
    (recommended) or the transcription key itself.
  - A billing key is kept like the other keys, sent only to api.deepgram.com, used only to read the balance (never to
    transcribe), and the balance is cached for 10 minutes.
- **Admin key guard.** If the Deepgram key you save turns out to be an Admin key, the page says so and offers to create
  a transcription-only key (scope `usage:write`) with it. It then either keeps the Admin key only for the balance, or
  forgets it.


## [0.3.0-alpha] - 2026-09-26

### Added

- **Paid speech-to-text within your monthly limit.** Deepgram or OpenAI can now be used by the nightly runs.
  - Every call is priced from the providers' published prices (shipped with the plugin, dated 2026-09-26), reserved
    against the month's limit in your currency before it is made, and recorded afterwards.
  - A call that would go over the limit, or whose cost can't be worked out (no price, no current exchange rates), isn't
    made.
  - The settings page shows this month's spending, per provider, and the exchange rates used. Test calls are counted
    too.

## [0.2.0-alpha] - 2026-09-26

### Added

- **Built-in speech-to-text.** Once an administrator allows it, the plugin downloads whisper.cpp (built and published
  by this project for Linux x64/arm64, Windows x64 and macOS) and a speech model (`base` by default, or `small`) the
  first time it's needed, and runs it on the server's CPU. Every file is checked against a SHA-256 compiled into the
  plugin, before it is first run and again before every run; downloads come only from this project's releases over
  HTTPS. Runs use below-normal priority, at most 8 threads and a time limit. The settings page has a **Built-in** box
  with **Test** (DOC-02).

### Changed

- Shared code updated (COM-03, COM-04, COM-05):
  - provider waits are bounded;
  - quota errors are recognised by the providers' own wording;
  - secrets are redacted before error text is shortened;
  - the key file is owner-only on Windows too;
  - "is this a local address" (where a key may go over plain HTTP) is the one shared definition.

### Fixed

- **SUB-09:** a subtitle replaced from outside (by another tool or a person) after this plugin changed it is treated as
  a new original. If it is in sync it offers no Undo, and if it is corrected, Undo brings back that replacement, never
  the older file. Each original now gets its own backup.
- **SUB-10:** results are no longer evicted after 2,000. They are the record of what was checked, changed and can be
  undone, so one is kept per subtitle file. Results for deleted files are dropped at the start of each run (unless the
  folder itself is missing, as with an offline share). Past a ceiling of 200,000, only results nothing depends on are
  dropped. Large libraries are now checked once each instead of the first 2,000 files being re-checked every night.
  Undo buttons, added subtitles and the 30-day search wait are never lost, and everything waiting for review is always
  listed.
- **SUB-04 (rest):** provider downloads are read with a limit and dropped as soon as they pass 10 MB, instead of being
  buffered in full first. A subtitle file on disk larger than 10 MB isn't read at all: it is recorded as **Too large**
  once and not looked at again until it changes.
- **SUB-11:** Apply and Undo errors now show the server's explanation (for example "The subtitle was changed after this
  plugin changed it"), not just "That couldn't be done".
- **SUB-12:** subtitle downloads per day default to 10 (a free OpenSubtitles account allows about 20, shared with
  manual downloads). When the provider says its allowance is used up, or it can't sign in, the search stops for the
  day. Any other provider error fails only that video's search, not the whole run.
- **SUB-13:** a forced-only subtitle track (signs and foreign-language parts) no longer counts as having subtitles in
  that language. A new setting decides whether picture-based tracks (PGS, VobSub) count (on by default).

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
