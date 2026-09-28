using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Api;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Candidates;
using Jellyfin.Plugin.Subtitles.Cleaning;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Discrepancy;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Generation;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.Sync;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

// More subtitle languages: every stage works for a subtitle in the audio's language, whatever it is, and a subtitle in
// another language than the audio is never lined up by words. Invented dialogue throughout.
public sealed class LanguageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-languages-" + Guid.NewGuid().ToString("N"));

    public LanguageTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- Which language the audio is in ----

    [Theory]
    [InlineData("fre", "eng", "fr")]
    [InlineData("fra", null, "fr")]
    [InlineData(null, "ger", "de")]
    [InlineData("und", "spa", "es")]
    [InlineData("zxx", "fre", "fr")]
    [InlineData("qqq", "ita", "it")]
    [InlineData(null, null, null)]
    public void The_audio_language_is_its_tag_or_else_the_library_language(string? tag, string? library, string? heard)
        => Assert.Equal(heard, SpokenLanguage.Heard(tag, library));

    [Fact]
    public void A_subtitle_matches_the_audio_only_when_both_are_known_and_the_same()
    {
        Assert.True(SpokenLanguage.Matches("fre", "fra", "eng"));
        Assert.True(SpokenLanguage.Matches("fr", null, "fre"));
        Assert.False(SpokenLanguage.Matches("fre", "eng", "fre"));
        Assert.False(SpokenLanguage.Matches("fre", null, null));
        Assert.False(SpokenLanguage.Matches(null, "eng", "eng"));

        Assert.True(SpokenLanguage.Differs("fre", "eng", "fre"));
        Assert.True(SpokenLanguage.Differs("spa", null, "eng"));
        Assert.False(SpokenLanguage.Differs("fre", "fra", "eng"));
        Assert.False(SpokenLanguage.Differs("fre", null, null));
    }

    [Fact]
    public void A_job_knows_which_language_speech_to_text_expects()
    {
        var job = new SubtitleJob(Guid.NewGuid(), "Film", "/v/Film.mkv", "/v/Film.fr.srt", "fre", TimeSpan.FromMinutes(90), 1, "fra");
        Assert.False(job.OtherLanguage);
        Assert.Equal("fr", job.SpeechLanguage);

        var untagged = job with { AudioLanguage = null, LibraryLanguage = "eng" };
        Assert.True(untagged.OtherLanguage);
        Assert.Equal("en", untagged.HeardLanguage);

        var unknown = job with { AudioLanguage = "und" };
        Assert.False(unknown.OtherLanguage);
        Assert.Equal("fr", unknown.SpeechLanguage);

        var find = new FindJob(Guid.NewGuid(), "Film", "/v/Film.mkv", new VideoFacts { FileName = "Film.mkv" }, "ger", TimeSpan.FromMinutes(90), 0, "deu");
        Assert.Equal("de", find.SpeechLanguage);
        Assert.Equal("de", (find with { AudioLanguage = null }).SpeechLanguage);

        var embedded = new EmbeddedJob(Guid.NewGuid(), "Film", "/v/Film.mkv", 3, "subrip", "spa", TimeSpan.FromMinutes(90), 0, "fp") { AudioLanguage = "spa" };
        Assert.Equal("es", embedded.SpeechLanguage);
    }

    [Fact]
    public void Only_languages_the_audio_is_in_are_searched_for()
    {
        Assert.True(FindRules.AudioIsIn("fre", "fre", "eng"));
        Assert.True(FindRules.AudioIsIn("eng", null, "eng"));
        Assert.False(FindRules.AudioIsIn("fre", null, "eng"));
        Assert.False(FindRules.AudioIsIn("fre", "eng", "fre"));
    }

    [Fact]
    public void Searches_are_counted_per_language_for_the_log()
    {
        Assert.Equal("fr 2, en 1", FindRules.PerLanguage(["fre", "eng", "fra"]));
        Assert.Equal("none", FindRules.PerLanguage([]));
    }

    [Fact]
    public void A_language_named_twice_in_different_forms_counts_once()
    {
        Assert.Equal(["fre", "eng"], LanguageSettings.Recognised(["fre", "French", "fra", "eng", "en", "English"]));
        Assert.Equal(["deu"], LanguageSettings.Recognised(["German", "ger"]));
    }

    [Fact]
    public void Languages_written_without_spaces_are_known()
    {
        Assert.True(SpokenLanguage.WrittenWithoutSpaces("chi"));
        Assert.True(SpokenLanguage.WrittenWithoutSpaces("ja"));
        Assert.False(SpokenLanguage.WrittenWithoutSpaces("kor"));
        Assert.False(SpokenLanguage.WrittenWithoutSpaces("fre"));
        Assert.Equal("French", SpokenLanguage.NameOf("fre"));
    }

    // ---- Checking a subtitle in another language than the audio ----

    private sealed class Counting : IAudioSource, ISpeechToText
    {
        public int Reads { get; private set; }

        public int Transcribed { get; private set; }

        public string Id => "fake";

        public Task<float[]> ReadAsync(TimeSpan start, TimeSpan length, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(new float[(int)(length.TotalSeconds * AudioFormat.SampleRate)]);
        }

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
        {
            Transcribed++;
            return Task.FromResult(new Transcript([], language, "fake", "fake", 60));
        }
    }

    // Synthetic audio: speech-band tones where lines are spoken (at subtitle time + offset), faint noise elsewhere
    private sealed class Onsets(IReadOnlyList<SubtitleCue> lines, double offset) : IAudioSource, ISpeechToText
    {
        public int Transcribed { get; private set; }

        public string Id => "fake";

        public Task<float[]> ReadAsync(TimeSpan start, TimeSpan length, CancellationToken cancellationToken)
        {
            var random = new Random(1 + (int)start.TotalSeconds);
            var n = (int)(length.TotalSeconds * AudioFormat.SampleRate);
            var samples = new float[n];
            var spoken = lines.Select(l => (From: l.Start.TotalSeconds + offset, To: l.End.TotalSeconds + offset)).ToList();
            var next = 0;
            for (var i = 0; i < n; i++)
            {
                var t = start.TotalSeconds + (i / (double)AudioFormat.SampleRate);
                var noise = (float)((random.NextDouble() * 2) - 1);
                while (next < spoken.Count && spoken[next].To <= t)
                {
                    next++;
                }

                samples[i] = next < spoken.Count && t >= spoken[next].From
                    ? (float)((0.15 * Math.Sin(2 * Math.PI * 440 * t)) + (0.1 * Math.Sin(2 * Math.PI * 1300 * t)) + (0.05 * noise))
                    : 0.004f * noise;
            }

            return Task.FromResult(samples);
        }

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
        {
            Transcribed++;
            return Task.FromResult(new Transcript([], language, "fake", "fake", 60));
        }
    }

    // Invented timing: lines of 1–4 s with gaps of 0.3–6 s over 25 minutes
    private static SubtitleDocument French()
    {
        var random = new Random(7);
        var cues = new List<SubtitleCue>();
        for (var t = 5.0; t < 1495;)
        {
            var length = 1 + (random.NextDouble() * 3);
            cues.Add(new SubtitleCue { Start = TimeSpan.FromSeconds(t), End = TimeSpan.FromSeconds(t + length), Text = "Réplique " + cues.Count });
            t += length + 0.3 + (random.NextDouble() * 5.7);
        }

        return new SubtitleDocument { Format = SubtitleFormat.Srt, Cues = cues };
    }

    private (SubtitleProcessor Processor, SubtitleJob Job, string Path) FrenchOnEnglish()
    {
        var path = Path.Combine(_dir, "Film.fr.srt");
        File.WriteAllBytes(path, SubtitleWriter.ToBytes(French()));
        var processor = new SubtitleProcessor(new ResultStore(Path.Combine(_dir, "results.json")), new SubtitleFiles(Path.Combine(_dir, "originals")));
        var job = new SubtitleJob(Guid.NewGuid(), "Invented Film", Path.Combine(_dir, "Film.mkv"), path, "fre", TimeSpan.FromMinutes(25), 0, "eng") { LibraryLanguage = "eng" };
        return (processor, job, path);
    }

    private static readonly Policies Automatic = new(ChangePolicy.Automatic, ChangePolicy.Automatic, new CleanupSettings());

    [Fact]
    public async Task A_subtitle_in_another_language_is_left_alone_and_marked()
    {
        var (processor, job, path) = FrenchOnEnglish();
        var before = File.ReadAllBytes(path);
        var fake = new Counting();

        var result = await processor.ProcessAsync(job, fake, fake, Automatic, CancellationToken.None);

        Assert.Equal(ResultStatus.OtherLanguage, result.Status);
        Assert.Equal(SyncCheck.OtherLanguageSkipped, result.Stage);
        Assert.Equal(0, fake.Reads);
        Assert.Equal(0, fake.Transcribed);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains("French", result.Explanation, StringComparison.Ordinal);
        Assert.Contains("English", result.Explanation, StringComparison.Ordinal);
        Assert.Equal("In another language than the audio, so its timing was left alone", ResultPresenter.Summary(result));
        Assert.Equal("Not the audio's language", ResultPresenter.StatusText(result.Status));

        // Not checked again on its own, until the experimental check is switched on (or the audio turns out to be French)
        var fingerprint = SubtitleFiles.Fingerprint(before);
        Assert.False(processor.NeedsCheck(job, fingerprint, "fake", timeOtherLanguages: false));
        Assert.True(processor.NeedsCheck(job, fingerprint, "fake", timeOtherLanguages: true));
        Assert.True(processor.NeedsCheck(job with { AudioLanguage = "fre" }, fingerprint, "fake", timeOtherLanguages: false));
        Assert.False(processor.NeedsAudit(path, fingerprint));
    }

    [Fact]
    public async Task By_speech_starts_a_subtitle_in_another_language_gets_a_correction_to_review_never_words()
    {
        var (processor, job, path) = FrenchOnEnglish();
        var before = File.ReadAllBytes(path);
        var fake = new Onsets(French().Cues, 3.0);

        var result = await processor.ProcessAsync(job, fake, fake, Automatic with { TimeOtherLanguages = true }, CancellationToken.None);

        Assert.Equal(0, fake.Transcribed);
        Assert.Equal(ResultStatus.Proposed, result.Status);
        Assert.Equal(SyncCheck.OtherLanguageStage, result.Stage);
        Assert.InRange(result.Offset, 2.5, 3.1);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Contains("never by words", result.Explanation, StringComparison.Ordinal);
        Assert.False(processor.NeedsCheck(job, SubtitleFiles.Fingerprint(before), "fake", timeOtherLanguages: true));
    }

    [Fact]
    public async Task By_speech_starts_an_unclear_answer_is_still_marked_as_another_language()
    {
        var (processor, job, _) = FrenchOnEnglish();
        var fake = new Counting();

        var result = await processor.ProcessAsync(job, fake, fake, Automatic with { TimeOtherLanguages = true }, CancellationToken.None);

        Assert.True(fake.Reads > 0);
        Assert.Equal(0, fake.Transcribed);
        Assert.Equal(ResultStatus.OtherLanguage, result.Status);
        Assert.Equal(SyncCheck.OtherLanguageStage, result.Stage);
    }

    [Fact]
    public async Task A_subtitle_checked_by_words_before_the_languages_were_told_apart_is_checked_again()
    {
        var (processor, job, path) = FrenchOnEnglish();
        var fake = new Counting();

        // As an earlier version saw it: the audio's language unknown, so compared (fruitlessly) with what was heard
        var earlier = await processor.ProcessAsync(job with { AudioLanguage = null, LibraryLanguage = null }, fake, fake, Automatic, CancellationToken.None);
        Assert.Equal(ResultStatus.Unreliable, earlier.Status);

        Assert.True(processor.NeedsCheck(job, SubtitleFiles.Fingerprint(File.ReadAllBytes(path)), "fake", timeOtherLanguages: false));
    }

    [Fact]
    public async Task The_same_language_hint_is_the_audio_language()
    {
        var path = Path.Combine(_dir, "Film.de.srt");
        File.WriteAllBytes(path, SubtitleWriter.ToBytes(French()));
        var processor = new SubtitleProcessor(new ResultStore(Path.Combine(_dir, "results.json")), new SubtitleFiles(Path.Combine(_dir, "originals")));
        var job = new SubtitleJob(Guid.NewGuid(), "Film", Path.Combine(_dir, "Film.mkv"), path, "ger", TimeSpan.FromMinutes(25), 0, null) { LibraryLanguage = "deu" };
        var heard = new List<string?>();
        var speech = new Listening(heard);

        await processor.ProcessAsync(job, new Counting(), speech, Automatic, CancellationToken.None);

        Assert.NotEmpty(heard);
        Assert.All(heard, l => Assert.Equal("de", l));
    }

    private sealed class Listening(List<string?> languages) : ISpeechToText
    {
        public string Id => "fake";

        public Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
        {
            languages.Add(language);
            return Task.FromResult(new Transcript([], language, "fake", "fake", 60));
        }
    }

    // ---- The language of a downloaded subtitle's text ----

    private const string English = "I don't know what you want from me. You said the boat was in the harbour, and it was not there. We have to go back and look again, because if we don't find it tonight, it is gone. Do you understand me? This is not a game, and I am not going to wait for you all night.";

    private const string French1 = "Je ne sais pas ce que tu veux de moi. Tu as dit que le bateau était dans le port, et il n'était pas là. Nous devons y retourner et chercher encore, parce que si on ne le trouve pas ce soir, c'est fini. Est-ce que tu me comprends? Ce n'est pas un jeu, et je ne vais pas t'attendre toute la nuit.";

    private const string German = "Ich weiß nicht, was du von mir willst. Du hast gesagt, das Boot ist im Hafen, und es war nicht da. Wir müssen zurück und noch einmal suchen, denn wenn wir es heute Nacht nicht finden, ist es weg. Verstehst du mich? Das ist kein Spiel, und ich werde nicht die ganze Nacht auf dich warten.";

    private const string Swedish = "Jag vet inte vad du vill ha av mig. Du sa att båten var i hamnen, och den var inte där. Vi måste gå tillbaka och leta igen, för om vi inte hittar den i kväll så är den borta. Förstår du mig? Det här är inte en lek, och jag tänker inte vänta på dig hela natten.";

    private const string Russian = "Я не знаю, чего ты от меня хочешь. Ты сказал, что лодка в гавани, а её там не было. Нам нужно вернуться и искать снова, потому что если мы не найдём её сегодня ночью, её не будет. Ты меня понимаешь? Это не игра, и я не буду ждать тебя всю ночь.";

    private static readonly VideoFacts Film = new() { FileName = "Rocket.Club.2019.1080p.BluRay.x264-KITE.mkv", Duration = TimeSpan.FromMinutes(110), FrameRate = 23.976 };

    private static SubtitleDocument Doc(string text)
        => new()
        {
            Format = SubtitleFormat.Srt,
            Cues = [.. Enumerable.Range(0, 900).Select(i => new SubtitleCue { Start = TimeSpan.FromMinutes(105) * i / 900, End = (TimeSpan.FromMinutes(105) * (i + 1) / 900) - TimeSpan.FromMilliseconds(100), Text = text })],
        };

    [Theory]
    [InlineData("fre")]
    [InlineData("fra")]
    [InlineData("fr")]
    [InlineData("French")]
    public void Any_form_of_the_language_code_is_recognised_in_the_text_check(string wanted)
    {
        var a = ContentChecks.Assess(Film, Doc(French1), wanted);
        Assert.False(a.Rejected);
        Assert.Contains("text is in the expected language", a.Reasons);
    }

    [Theory]
    [InlineData(German, "deu")]
    [InlineData(German, "ger")]
    [InlineData(Swedish, "swe")]
    [InlineData(Russian, "rus")]
    public void Text_in_the_wanted_language_passes(string text, string wanted)
        => Assert.False(ContentChecks.Assess(Film, Doc(text), wanted).Rejected);

    [Theory]
    [InlineData(English, "deu")]
    [InlineData(English, "rus")]
    [InlineData(Russian, "eng")]
    [InlineData(German, "fra")]
    [InlineData(English, "swe")]
    public void Text_in_another_language_is_rejected(string text, string wanted)
        => Assert.True(ContentChecks.Assess(Film, Doc(text), wanted).Rejected);

    [Fact]
    public void A_close_relative_or_a_language_the_guesser_does_not_know_is_not_rejected()
    {
        // Swedish text offered as Norwegian or Danish: too close to tell apart by common words
        Assert.False(ContentChecks.Assess(Film, Doc(Swedish), "nob").Rejected);
        Assert.False(ContentChecks.Assess(Film, Doc(Swedish), "dan").Rejected);

        // Hungarian's common words aren't known: Latin letters are all that can be said
        Assert.False(ContentChecks.Assess(Film, Doc(English), "hun").Rejected);
    }

    [Fact]
    public void Scripts_are_told_apart()
    {
        Assert.Equal(Script.Cyrillic, LanguageGuesser.ScriptOf(string.Concat(Enumerable.Repeat(Russian, 3))));
        Assert.Equal(Script.Latin, LanguageGuesser.ScriptOf(string.Concat(Enumerable.Repeat(German, 3))));
        Assert.Equal(Script.Cjk, LanguageGuesser.ScriptOf(string.Concat(Enumerable.Repeat("我不知道你想从我这里得到什么。你说船在港口里，可是它不在那里。", 10))));
        Assert.Null(LanguageGuesser.ScriptOf("Too short."));
        Assert.Null(LanguageGuesser.ScriptFor("sr"));
        Assert.Equal(Script.Hangul, LanguageGuesser.ScriptFor("ko"));
    }

    [Theory]
    [InlineData("pt", "PT,BR_PT")]
    [InlineData("nb", "NO")]
    [InlineData("zh", "ZH,ZH_BG")]
    [InlineData("fr", "FR")]
    public void SubDL_is_asked_in_its_own_codes(string language, string codes)
        => Assert.Equal(codes, SubDlSource.LanguageCodes(language));

    // ---- Comparing words: negations and numbers by language ----

    private static readonly (double Start, string Text)[] Spanish =
    [
        (1, "Buenos días a todos."),
        (5, "El tren sale a mediodía."),
        (9, "¿Margarita preparó la maleta azul?"),
        (13, "La dejé junto a la puerta."),
        (17, "Entonces debemos darnos prisa."),
        (21, "Dile a Catalina que espere fuera."),
        (25, "Tengo cinco billetes aquí."),
        (29, "Podemos llegar a tiempo."),
        (33, "Alguien vio el mapa viejo."),
        (37, "Vámonos antes de que llueva."),
    ];

    private static (double, string)[] With(int line, string text)
    {
        var copy = Spanish.ToArray();
        copy[line] = (copy[line].Start, text);
        return copy;
    }

    private static DiscrepancyReport Find(IEnumerable<(double Start, string Text)> said, IEnumerable<(double Start, string Text)> heard, string language)
        => DiscrepancyFinder.Find(DiscrepancyTests.Subtitle(said), DiscrepancyTests.Said(heard), t => t, new DiscrepancyOptions { MinConfidence = 0, Language = language });

    [Fact]
    public void A_negation_is_counted_in_the_subtitle_language()
    {
        var report = Find(Spanish, With(8, "Nadie vio el mapa viejo."), "es");
        Assert.Equal(DiscrepancyFinder.Negation, Assert.Single(report.Findings).Kind);
        Assert.Contains("no, nunca, nada", report.Findings[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_language_without_a_negation_list_is_never_flagged_for_one()
    {
        var report = Find(Spanish, With(8, "Nadie vio el mapa viejo."), "eu");
        Assert.DoesNotContain(report.Findings, f => f.Kind == DiscrepancyFinder.Negation);
        Assert.Empty(SpokenText.NegationsFor("eu"));
    }

    [Fact]
    public void A_number_written_in_words_is_not_a_difference_where_number_words_are_not_read()
    {
        var report = Find(Spanish, With(6, "Tengo 5 billetes aquí."), "es");
        Assert.DoesNotContain(report.Findings, f => f.Kind == DiscrepancyFinder.Number);
    }

    [Fact]
    public void A_number_in_the_place_of_another_is_still_a_difference_in_any_language()
    {
        var report = Find(With(6, "Tengo 5 billetes aquí."), With(6, "Tengo 4 billetes aquí."), "es");
        Assert.Equal(DiscrepancyFinder.Number, Assert.Single(report.Findings).Kind);
    }

    [Fact]
    public void Negations_are_language_aware()
    {
        static bool[] Negated(string text, string language) => [.. SpokenText.Tokens(text.Split(' '), language, false).Select(t => t.Negation)];

        Assert.Equal([false, false, false, true], Negated("Je ne sais pas", "fr"));
        Assert.Equal([false, false, true], Negated("Ich weiß nicht", "de"));
        Assert.Equal([false, false], Negated("No, gracias.", "es"));
        Assert.Equal([true, false], Negated("No quiero", "es"));
        Assert.Equal([false, false], Negated("not here", "fr"));
        Assert.True(SpokenText.ReadsNumberWords("en"));
        Assert.False(SpokenText.ReadsNumberWords("fr"));
    }

    // ---- Lining up by words in a language written without spaces ----

    [Fact]
    public void Chinese_is_lined_up_character_by_character()
    {
        string[] lines = ["我今天早上去了市场", "你买了什么东西呢", "我买了三个红苹果", "明天我们一起去公园", "天气预报说会下雨", "那我们带上雨伞吧", "好主意我去拿伞", "别忘了关上窗户", "我已经关好了窗户", "那我们出发吧朋友"];
        var cues = lines.Select((l, i) => new SubtitleCue { Start = TimeSpan.FromSeconds(10 + (i * 4)), End = TimeSpan.FromSeconds(12 + (i * 4)), Text = l }).ToList();
        var document = new SubtitleDocument { Format = SubtitleFormat.Srt, Cues = cues };

        // Heard 2 s later, as words of two or three characters (as speech-to-text splits Chinese)
        var words = new List<TranscribedWord>();
        for (var i = 0; i < lines.Length; i++)
        {
            var at = 12.0 + (i * 4);
            for (var k = 0; k < lines[i].Length; k += 3)
            {
                var piece = lines[i].Substring(k, Math.Min(3, lines[i].Length - k));
                words.Add(new TranscribedWord(piece, at + (k * 0.2), at + (k * 0.2) + (piece.Length * 0.2), 0.9));
            }
        }

        var anchors = TranscriptAligner.Anchors(document, [(0, new Transcript(words, "zh", "fake", "fake", 60))]);
        var model = TranscriptAligner.Solve(anchors, wordLag: 0);

        Assert.True(anchors.Count >= TranscriptAligner.MinimumAnchors);
        Assert.Equal(SyncStatus.Corrected, model.Status);
        Assert.Equal(2.0, model.Offset, 0.3);
    }

    // ---- Generated subtitles ----

    [Fact]
    public void Chinese_lines_are_short_and_wrap_between_characters()
    {
        var rules = CueRules.For("zh");
        Assert.Equal(16, rules.MaxLineLength);
        Assert.Same(CueRules.Default, CueRules.For("fr"));
        Assert.Equal("我今天早上去了市场，\n你买了什么东西呢我也想去", TranscriptCues.Wrap("我今天早上去了市场，你买了什么东西呢我也想去", 12));
        Assert.Null(TranscriptCues.Wrap("我今天早上去了市场你买了什么东西呢我也想去看看", 10));
    }

    [Fact]
    public void Korean_words_keep_their_spaces()
        => Assert.Equal("안녕하세요 저는 학생입니다", TranscriptCues.Join([new TranscribedWord("안녕하세요", 0, 1, 1), new TranscribedWord("저는", 1, 2, 1), new TranscribedWord("학생입니다", 2, 3, 1)]));

    [Theory]
    [InlineData("fre", "/v/Film.mkv", "/v/Film.fr.generated.srt")]
    [InlineData("ger", "/v/Film.mkv", "/v/Film.de.generated.srt")]
    [InlineData("chi", "/v/Film.mkv", "/v/Film.zh.generated.srt")]
    public void Generated_files_are_labelled_with_the_two_letter_code(string language, string video, string expected)
        => Assert.Equal(expected, SubtitleGenerator.PathFor(video, language));

    // ---- Clean-up ----

    [Fact]
    public void Speaker_labels_and_descriptions_are_found_in_any_alphabet()
    {
        Assert.Equal("Hola", SubtitleCleaner.StripHearingImpaired("JOSÉ: Hola"));
        Assert.Equal("Привет", SubtitleCleaner.StripHearingImpaired("ДИМА: Привет"));
        Assert.Equal("你好", SubtitleCleaner.StripHearingImpaired("（笑）你好"));
        Assert.Equal("你好", SubtitleCleaner.StripHearingImpaired("【音乐】你好"));
    }

    [Theory]
    [InlineData("Sous-titres par l'équipe Invention")]
    [InlineData("Subtítulos por Equipo Inventado")]
    [InlineData("Untertitel: Erfundenes Team")]
    [InlineData("Legendas por Equipe Inventada")]
    [InlineData("Ondertiteling door Verzonnen Team")]
    [InlineData("Tłumaczenie: Wymyślony Zespół")]
    public void Credits_in_other_languages_are_removed_near_the_ends(string credit)
    {
        var cue = new SubtitleCue { Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(2), Text = credit };
        Assert.True(SubtitleCleaner.IsAdvert(cue, 0, 100));
        Assert.False(SubtitleCleaner.IsAdvert(cue, 50, 100));
    }

    // ---- Results per language ----

    private static SubtitleResult R(string id, string path, ResultStatus status) => new() { Id = id, SubtitlePath = path, Status = status };

    [Fact]
    public void Results_are_counted_and_filtered_per_language()
    {
        IReadOnlyList<SubtitleResult> all =
        [
            R("a", "/v/A.en.srt", ResultStatus.InSync),
            R("find-b", "/v/B.en.srt", ResultStatus.NotFound),
            R("find-c", "/v/C.fr.srt", ResultStatus.NotFound),
            R("d", "/v/D.fr.sdh.srt", ResultStatus.OtherLanguage),
            R("emb-e", "/v/Ice.Age.mkv", ResultStatus.InSync),
        ];

        var tally = ResultQuery.Tally(all);

        Assert.Equal(2, tally.Languages.Count);
        var fr = tally.Languages.Single(l => l.Code == "FR");
        Assert.Equal(("French", 2, 1, 1), (fr.Name, fr.Results, fr.Missing, fr.OtherLanguage));
        Assert.Equal(1, tally.Languages.Single(l => l.Code == "EN").Missing);
        Assert.Null(ResultQuery.LanguageOf(all[4]));
        Assert.Equal(["find-c", "d"], all.Where(r => ResultQuery.Matches(r, "lang:fr", null)).Select(r => r.Id));
    }
}
