// Calibrates the whole-file check (DiscrepancyFinder) on real videos. All paths and addresses come from arguments; the
// tool reads the videos and subtitles it is given and writes only under the output and cache folders it is given.
//
//   damage     --in S.srt --out D.srt --truth D.truth.json [--seed 1] [--deletes 5] [--numbers 5] [--negations 3] [--names 3] [--extras 3]
//   transcribe --manifest M.json --service http://host:port/v1 --cache DIR [--model NAME] [--ffmpeg PATH] [--ffprobe PATH] [--only ID]
//   evaluate   --manifest M.json --cache DIR --out DIR [--model NAME] [--tolerance 2,3,4] [--ratio 0.4,0.5,0.6]
//              [--missing 4:1.5,6:2.5] [--extra 3,5] [--words-min 4] [--confidence 0.741] [--anchored false,true]
//              [--known-names false,true] [--distinct false,true] [--isolated false,true] [--detail CONFIG,...|all]
//
// Manifest: [{ "Id": "g01", "Group": "good|wrong|damaged", "Video": "/abs/video", "Subtitle": "/abs/sub.srt",
//              "Language": "en", "AudioStream": 0, "Truth": "/abs/sub.truth.json" (damaged only) }]
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Subtitles.Audio;
using Jellyfin.Plugin.Subtitles.Discrepancy;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Generation;
using Jellyfin.Plugin.Subtitles.SpeechToText;

namespace DiscrepancyEval;

public sealed record Entry(string Id, string Group, string Video, string Subtitle, string Language, int AudioStream = 0, string? Truth = null);

public sealed record TruthItem(string Kind, double Start, double End, string Before, string After);

public sealed record CachedTranscript(string Video, int AudioStream, string Language, string Model, string Provider, double DurationSeconds, double SecondsSent, double ElapsedSeconds, List<double[]> Times, List<string> Texts);

public sealed record Config(string Name, DiscrepancyOptions Options);

public static class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: damage | transcribe | evaluate (see Program.cs header)");
            return 2;
        }

        var a = Args(args.Skip(1).ToArray());
        return args[0] switch
        {
            "damage" => Damage(a),
            "transcribe" => await Transcribe(a).ConfigureAwait(false),
            "evaluate" => Evaluate(a),
            _ => 2,
        };
    }

    private static Dictionary<string, string> Args(string[] args)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                d[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            }
        }

        return d;
    }

    private static string Get(Dictionary<string, string> a, string key, string? fallback = null)
        => a.TryGetValue(key, out var v) ? v : fallback ?? throw new ArgumentException("missing --" + key);

    // Capitalised words that aren't a character's name
    private static readonly HashSet<string> Titles = new(StringComparer.Ordinal)
    {
        "The", "God", "Mom", "Mum", "Dad", "Daddy", "Mommy", "Mummy", "Sir", "Madam", "Honour", "Honor", "Lord", "Jesus", "Christ",
        "Mister", "Miss", "Doctor", "Captain", "Commander", "Colonel", "Sergeant", "Lieutenant", "Major", "General", "Officer",
        "Detective", "Agent", "Father", "Mother", "Uncle", "Aunt", "Grandma", "Grandpa", "President", "Judge", "North", "South",
        "East", "West", "Christmas", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "English",
    };

    private static SubtitleDocument Read(string path)
    {
        var (text, _) = SubtitleEncoding.Decode(File.ReadAllBytes(path));
        return SubtitleReader.Parse(text, SubtitleReader.Detect(path, text) ?? SubtitleFormat.Srt);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Synthetic damage: deleted lines, changed numbers, flipped negations, spaced apart, with the truth written beside
    // ---------------------------------------------------------------------------------------------------------------
    private static int Damage(Dictionary<string, string> a)
    {
        var doc = Read(Get(a, "in"));
        var rng = new Random(int.Parse(Get(a, "seed", "1"), Inv));
        var cues = doc.Cues.ToList();
        var taken = new HashSet<int>();
        bool Free(int i) => !Enumerable.Range(i - 2, 5).Any(taken.Contains) && !SpokenText.IsMusic(cues[i].Text);

        // Only spoken words count: never inside sound descriptions or speaker labels in brackets
        static bool Spoken(string text, Match m) => m.Success && !Regex.Matches(text, @"\[[^\]]*\]|\([^)]*\)").Any(b => m.Index >= b.Index && m.Index < b.Index + b.Length);
        var truth = new List<TruthItem>();
        var order = Enumerable.Range(0, cues.Count).OrderBy(_ => rng.Next()).ToList();

        // Numbers
        var numberWords = new[] { "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "twenty", "thirty", "forty", "fifty", "hundred", "thousand" };
        var wanted = int.Parse(Get(a, "numbers", "5"), Inv);
        foreach (var i in order.Where(i => Free(i)))
        {
            if (truth.Count(t => t.Kind == DiscrepancyFinder.Number) >= wanted)
            {
                break;
            }

            var text = cues[i].Text;
            string? changed = null;
            var digits = Regex.Match(text, @"(?<![\d:.,])\d{1,4}(?![\d:.,]\d)");
            if (Spoken(text, digits))
            {
                var n = int.Parse(digits.Value, Inv);
                changed = text[..digits.Index] + (n + 3 + rng.Next(5)).ToString(Inv) + text[(digits.Index + digits.Length)..];
            }
            else
            {
                foreach (var w in numberWords)
                {
                    var m = Regex.Match(text, @"\b" + w + @"\b", RegexOptions.IgnoreCase);
                    if (Spoken(text, m))
                    {
                        var other = numberWords.Where(x => x != w && x != "hundred" && x != "thousand").ElementAt(rng.Next(numberWords.Length - 3));
                        changed = text[..m.Index] + other + text[(m.Index + m.Length)..];
                        break;
                    }
                }
            }

            if (changed is not null)
            {
                truth.Add(new TruthItem(DiscrepancyFinder.Number, cues[i].Start.TotalSeconds, cues[i].End.TotalSeconds, text, changed));
                cues[i] = cues[i] with { Text = changed };
                taken.Add(i);
            }
        }

        // Negations: a "not" or "n't" removed
        wanted = int.Parse(Get(a, "negations", "3"), Inv);
        var contractions = new (string From, string To)[] { ("can't", "can"), ("won't", "will"), ("don't", "do"), ("didn't", "did"), ("doesn't", "does"), ("isn't", "is"), ("wasn't", "was"), ("aren't", "are"), ("haven't", "have"), ("couldn't", "could"), ("wouldn't", "would"), ("shouldn't", "should") };
        foreach (var i in order.Where(i => Free(i)))
        {
            if (truth.Count(t => t.Kind == DiscrepancyFinder.Negation) >= wanted)
            {
                break;
            }

            var text = cues[i].Text;
            string? changed = null;
            var not = Regex.Match(text, @"\snot\b", RegexOptions.IgnoreCase);
            if (Spoken(text, not))
            {
                changed = text[..not.Index] + text[(not.Index + not.Length)..];
            }
            else
            {
                foreach (var (from, to) in contractions)
                {
                    var m = Regex.Match(text, @"\b" + Regex.Escape(from) + @"\b", RegexOptions.IgnoreCase);
                    if (Spoken(text, m))
                    {
                        var replacement = char.IsUpper(m.Value[0]) ? char.ToUpperInvariant(to[0]) + to[1..] : to;
                        changed = text[..m.Index] + replacement + text[(m.Index + m.Length)..];
                        break;
                    }
                }
            }

            if (changed is not null)
            {
                truth.Add(new TruthItem(DiscrepancyFinder.Negation, cues[i].Start.TotalSeconds, cues[i].End.TotalSeconds, text, changed));
                cues[i] = cues[i] with { Text = changed };
                taken.Add(i);
            }
        }

        // Names: a character's name (written with a capital mid-sentence in at least three lines) replaced by another's
        wanted = int.Parse(Get(a, "names", "3"), Inv);
        var nameCounts = cues.SelectMany(c => Regex.Matches(c.Text, @"(?<=[a-z,] )[A-Z][a-z]{2,}\b").Select(m => m.Value).Distinct())
            .GroupBy(x => x).Where(g => g.Count() >= 3).Select(g => g.Key)
            .Where(x => !Titles.Contains(x) && !cues.Any(c => Regex.IsMatch(c.Text, @"\b" + x.ToLowerInvariant() + @"\b")))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (var i in order.Where(i => Free(i) && nameCounts.Count >= 2))
        {
            if (truth.Count(t => t.Kind == DiscrepancyFinder.Name) >= wanted)
            {
                break;
            }

            var text = cues[i].Text;
            var m = Regex.Match(text, @"(?<=[a-z,] )(" + string.Join('|', nameCounts) + @")\b");
            if (Spoken(text, m))
            {
                var other = nameCounts.Where(x => x != m.Value).ElementAt(rng.Next(nameCounts.Count - 1));
                var changed = text[..m.Index] + other + text[(m.Index + m.Length)..];
                truth.Add(new TruthItem(DiscrepancyFinder.Name, cues[i].Start.TotalSeconds, cues[i].End.TotalSeconds, text, changed));
                cues[i] = cues[i] with { Text = changed };
                taken.Add(i);
            }
        }

        // Deleted lines: spoken lines of at least 4 words and 1.5 s
        wanted = int.Parse(Get(a, "deletes", "5"), Inv);
        var deleted = new HashSet<int>();
        foreach (var i in order.Where(i => Free(i)))
        {
            if (deleted.Count >= wanted)
            {
                break;
            }

            var c = cues[i];
            if (!SpokenText.IsMusic(c.Text) && SpokenText.SubtitleWords(c.Text).Count >= 4 && (c.End - c.Start).TotalSeconds >= 1.5)
            {
                truth.Add(new TruthItem(DiscrepancyFinder.MissingLine, c.Start.TotalSeconds, c.End.TotalSeconds, c.Text, string.Empty));
                deleted.Add(i);
                taken.Add(i);
            }
        }

        // Extra lines: a long line from elsewhere in the file put in a silent gap of at least 8 s between lines
        wanted = int.Parse(Get(a, "extras", "3"), Inv);
        var added = new List<SubtitleCue>();
        var donors = order.Where(i => !SpokenText.IsMusic(cues[i].Text) && SpokenText.SubtitleWords(cues[i].Text).Count >= 8).ToList();
        foreach (var i in order.Where(i => i + 1 < cues.Count && (cues[i + 1].Start - cues[i].End).TotalSeconds >= 8 && Free(i) && Free(i + 1)))
        {
            if (added.Count >= Math.Min(wanted, donors.Count))
            {
                break;
            }

            var donor = cues[donors[added.Count]];
            var start = cues[i].End + ((cues[i + 1].Start - cues[i].End) / 2) - TimeSpan.FromSeconds(1.25);
            added.Add(new SubtitleCue { Start = start, End = start + TimeSpan.FromSeconds(2.5), Text = donor.Text });
            truth.Add(new TruthItem(DiscrepancyFinder.Extra, start.TotalSeconds, start.TotalSeconds + 2.5, string.Empty, donor.Text));
            taken.Add(i);
        }

        var damaged = doc with { Format = SubtitleFormat.Srt, Cues = cues.Where((_, i) => !deleted.Contains(i)).Concat(added).OrderBy(c => c.Start).ToList() };
        File.WriteAllText(Get(a, "out"), SubtitleWriter.Write(damaged), new UTF8Encoding(false));
        File.WriteAllText(Get(a, "truth"), JsonSerializer.Serialize(truth.OrderBy(t => t.Start).ToList(), Json));
        Console.WriteLine($"{added.Count} extra, {truth.Count(t => t.Kind == DiscrepancyFinder.Name)} names, {truth.Count(t => t.Kind == DiscrepancyFinder.MissingLine)} deleted, {truth.Count(t => t.Kind == DiscrepancyFinder.Number)} numbers, {truth.Count(t => t.Kind == DiscrepancyFinder.Negation)} negations");
        return 0;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Transcripts: each video once, cached on disk
    // ---------------------------------------------------------------------------------------------------------------
    private static string CacheFile(string cache, Entry e, string model)
    {
        var info = new FileInfo(e.Video);
        var key = string.Join('|', e.Video, info.Length.ToString(Inv), info.LastWriteTimeUtc.Ticks.ToString(Inv), e.AudioStream.ToString(Inv), e.Language, model);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20].ToLowerInvariant();
        return Path.Combine(cache, hash + ".json.gz");
    }

    private static CachedTranscript? Load(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        using var gz = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<CachedTranscript>(gz);
    }

    private static List<Entry> Manifest(Dictionary<string, string> a)
        => JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(Get(a, "manifest"))) ?? [];

    private static async Task<double> Duration(string ffprobe, string video)
    {
        var psi = new ProcessStartInfo(ffprobe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", "file:" + video })
        {
            psi.ArgumentList.Add(x);
        }

        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await p.WaitForExitAsync().ConfigureAwait(false);
        return double.Parse(output.Trim(), Inv);
    }

    private static async Task<int> Transcribe(Dictionary<string, string> a)
    {
        var cache = Get(a, "cache");
        Directory.CreateDirectory(cache);
        var model = Get(a, "model", string.Empty);
        var ffmpeg = Get(a, "ffmpeg", "/usr/lib/jellyfin-ffmpeg/ffmpeg");
        var ffprobe = Get(a, "ffprobe", Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe"));
        var only = a.TryGetValue("only", out var o) ? o.Split(',').ToHashSet() : null;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        var speech = new OpenAiCompatibleSpeechToText(http, "local", new Uri(Get(a, "service")), null, model, withSegments: true);
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Manifest(a).Where(e => only is null || only.Contains(e.Id)))
        {
            var file = CacheFile(cache, e, model);
            if (!done.Add(file) || File.Exists(file))
            {
                Console.WriteLine($"{e.Id}: cached");
                continue;
            }

            var duration = await Duration(ffprobe, e.Video).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            var full = await FullTranscriber.TranscribeAsync(new FfmpegAudioSource(ffmpeg, e.Video, e.AudioStream), TimeSpan.FromSeconds(duration), speech, e.Language, CancellationToken.None).ConfigureAwait(false);
            var cached = new CachedTranscript(e.Video, e.AudioStream, e.Language, full.Model, full.Provider, duration, full.SecondsSent, watch.Elapsed.TotalSeconds,
                full.Words.Select(w => new[] { w.Start, w.End, w.Confidence ?? -1 }).ToList(), full.Words.Select(w => w.Text).ToList());
            var tmp = file + ".tmp";
            using (var gz = new GZipStream(File.Create(tmp), CompressionLevel.Optimal))
            {
                JsonSerializer.Serialize(gz, cached);
            }

            File.Move(tmp, file, true);
            Console.WriteLine(string.Create(Inv, $"{e.Id}: {duration / 60:0.0} min, {full.Words.Count} words, {watch.Elapsed.TotalSeconds:0} s ({duration / watch.Elapsed.TotalSeconds:0.0}x real time)"));
        }

        return 0;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Evaluation: every file against a grid of options
    // ---------------------------------------------------------------------------------------------------------------
    private static List<double> Doubles(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, Inv)).ToList();

    private static List<Config> Grid(Dictionary<string, string> a)
    {
        var configs = new List<Config>();
        var floor = ConfidenceCalibration.WhisperFloor;
        foreach (var t in Doubles(Get(a, "tolerance", "2,3,4")))
        foreach (var r in Doubles(Get(a, "ratio", "0.4,0.5,0.6")))
        foreach (var m in Get(a, "missing", "4:1.5,6:2.5").Split(','))
        foreach (var x in Doubles(Get(a, "extra", "3,5")))
        foreach (var wm in Doubles(Get(a, "words-min", "4")))
        foreach (var c in Doubles(Get(a, "confidence", floor.ToString(Inv))))
        foreach (var anchored in Get(a, "anchored", "false,true").Split(',').Select(bool.Parse))
        foreach (var known in Get(a, "known-names", "false,true").Split(',').Select(bool.Parse))
        foreach (var distinct in Get(a, "distinct", "false,true").Split(',').Select(bool.Parse))
        foreach (var isolated in Get(a, "isolated", "false,true").Split(',').Select(bool.Parse))
        {
            var mw = int.Parse(m.Split(':')[0], Inv);
            var ms = double.Parse(m.Split(':')[1], Inv);
            var name = string.Create(Inv, $"t{t}_r{r}_m{mw}-{ms}_x{x}_w{wm}_c{c:0.###}_a{(anchored ? 1 : 0)}_k{(known ? 1 : 0)}_d{(distinct ? 1 : 0)}_i{(isolated ? 1 : 0)}");
            configs.Add(new Config(name, new DiscrepancyOptions
            {
                Tolerance = t, MissingWordRatio = r, MissingMinWords = mw, MissingMinSeconds = ms, MinExtraWords = (int)x,
                MissingWordsMin = (int)wm, MinConfidence = c, Anchored = anchored, KnownNames = known, Distinct = distinct, Isolated = isolated,
            }));
        }

        return configs;
    }

    private static string Csv(object? v)
    {
        var s = v switch
        {
            null => string.Empty,
            double d => d.ToString("0.###", Inv),
            IFormattable f => f.ToString(null, Inv),
            _ => v.ToString() ?? string.Empty,
        };
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
    }

    private static string Row(params object?[] values) => string.Join(',', values.Select(Csv));

    private static int Evaluate(Dictionary<string, string> a)
    {
        var cache = Get(a, "cache");
        var outDir = Get(a, "out");
        Directory.CreateDirectory(outDir);
        var model = Get(a, "model", string.Empty);
        var configs = Grid(a);
        var detail = a.TryGetValue("detail", out var dd) ? dd.Split(',').ToHashSet() : [configs[0].Name];
        var kinds = DiscrepancyFinder.Kinds;
        var files = new StringBuilder(Row("config", "id", "group", "hours", "problem", "shared", "suppressed", "total") + "," + string.Join(',', kinds) + ",finder_ms,transcribe_s\n");
        var findings = new StringBuilder(Row("config", "id", "group", "kind", "cue", "start", "end", "confidence", "line", "heard", "reason") + "\n");
        var recall = new StringBuilder(Row("config", "id", "kind", "start", "before", "after", "same_kind", "any_kind", "found_kinds") + "\n");

        // Aggregates: config -> group -> numbers
        var agg = new Dictionary<string, Dictionary<string, Agg>>(StringComparer.Ordinal);
        foreach (var e in Manifest(a))
        {
            var t = Load(CacheFile(cache, e, model));
            if (t is null)
            {
                Console.Error.WriteLine($"{e.Id}: no transcript, skipped");
                continue;
            }

            var words = t.Texts.Select((w, i) => new TranscribedWord(w, t.Times[i][0], t.Times[i][1], t.Times[i][2] < 0 ? null : t.Times[i][2])).ToList();
            var doc = Read(e.Subtitle);
            var truth = e.Truth is null ? [] : JsonSerializer.Deserialize<List<TruthItem>>(File.ReadAllText(e.Truth)) ?? [];
            var hours = t.DurationSeconds / 3600;
            foreach (var c in configs)
            {
                var watch = Stopwatch.StartNew();
                var report = DiscrepancyFinder.Find(doc, words, x => x, c.Options with { Language = e.Language });
                var ms = watch.Elapsed.TotalMilliseconds;
                var counts = report.Counts;
                files.Append(Row(c.Name, e.Id, e.Group, hours, report.Problem is null ? string.Empty : "guard", report.Shared, report.Suppressed, report.Findings.Count))
                    .Append(',').Append(string.Join(',', kinds.Select(k => counts.GetValueOrDefault(k))))
                    .Append(',').Append(Row(ms, t.ElapsedSeconds)).Append('\n');
                var g = agg.TryGetValue(c.Name, out var gg) ? gg : agg[c.Name] = new(StringComparer.Ordinal);
                var s = g.TryGetValue(e.Group, out var ss) ? ss : g[e.Group] = new Agg();
                s.Files++;
                s.Hours += hours;
                s.Guards += report.Problem is null ? 0 : 1;
                s.Suppressed += report.Suppressed;
                foreach (var k in kinds)
                {
                    s.Kinds[k] = s.Kinds.GetValueOrDefault(k) + counts.GetValueOrDefault(k);
                }

                if (detail.Contains(c.Name) || detail.Contains("all"))
                {
                    foreach (var f in report.Findings)
                    {
                        findings.Append(Row(c.Name, e.Id, e.Group, f.Kind, f.Cue, f.AudioStart, f.AudioEnd, f.Confidence, f.Cue >= 0 ? doc.Cues[f.Cue].Text.Replace('\n', ' ') : string.Empty, f.Heard.Replace('\n', ' '), f.Reason)).Append('\n');
                    }

                    if (report.Problem is not null)
                    {
                        findings.Append(Row(c.Name, e.Id, e.Group, "GUARD", -1, 0, 0, null, string.Empty, string.Empty, report.Problem)).Append('\n');
                    }
                }

                foreach (var item in truth)
                {
                    // A line's findings are placed at its (moved) time; a missing line at the words heard
                    bool Near(LineDiscrepancy f) => f.AudioStart < item.End + 1.0 && f.AudioEnd > item.Start - 1.0;
                    var near = report.Findings.Where(Near).ToList();
                    var same = near.Any(f => f.Kind == item.Kind);
                    var any = near.Count > 0;
                    s.Truth[item.Kind] = s.Truth.GetValueOrDefault(item.Kind) + 1;
                    s.Same[item.Kind] = s.Same.GetValueOrDefault(item.Kind) + (same ? 1 : 0);
                    s.Any[item.Kind] = s.Any.GetValueOrDefault(item.Kind) + (any ? 1 : 0);
                    recall.Append(Row(c.Name, e.Id, item.Kind, item.Start, item.Before.Replace('\n', ' '), item.After.Replace('\n', ' '), same ? 1 : 0, any ? 1 : 0, string.Join(' ', near.Select(f => f.Kind)))).Append('\n');
                }
            }

            Console.Error.WriteLine($"{e.Id}: done");
        }

        File.WriteAllText(Path.Combine(outDir, "files.csv"), files.ToString());
        File.WriteAllText(Path.Combine(outDir, "findings.csv"), findings.ToString());
        File.WriteAllText(Path.Combine(outDir, "recall.csv"), recall.ToString());

        var summary = new StringBuilder(Row("config", "group", "files", "hours", "guards", "suppressed", "per_hour") + "," + string.Join(',', kinds.Select(k => k + "_per_hour")) + "," + string.Join(',', kinds.Select(k => k + "_recall")) + "," + string.Join(',', kinds.Select(k => k + "_recall_any")) + "\n");
        foreach (var (config, groups) in agg)
        {
            foreach (var (group, s) in groups)
            {
                var total = s.Kinds.Values.Sum();
                summary.Append(Row(config, group, s.Files, s.Hours, s.Guards, s.Suppressed, total / s.Hours))
                    .Append(',').Append(string.Join(',', kinds.Select(k => Csv(s.Kinds.GetValueOrDefault(k) / s.Hours))))
                    .Append(',').Append(string.Join(',', kinds.Select(k => s.Truth.TryGetValue(k, out var n) && n > 0 ? Csv(s.Same.GetValueOrDefault(k) / (double)n) : string.Empty)))
                    .Append(',').Append(string.Join(',', kinds.Select(k => s.Truth.TryGetValue(k, out var n) && n > 0 ? Csv(s.Any.GetValueOrDefault(k) / (double)n) : string.Empty)))
                    .Append('\n');
            }
        }

        File.WriteAllText(Path.Combine(outDir, "summary.csv"), summary.ToString());
        Console.WriteLine($"{configs.Count} configs; wrote files.csv, findings.csv, recall.csv, summary.csv to {outDir}");
        return 0;
    }

    private sealed class Agg
    {
        public int Files { get; set; }

        public double Hours { get; set; }

        public int Guards { get; set; }

        public int Suppressed { get; set; }

        public Dictionary<string, int> Kinds { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Truth { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Same { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Any { get; } = new(StringComparer.Ordinal);
    }
}
