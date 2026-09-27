// Calibrates the piecewise timing fit (PiecewiseAligner) on real videos: subtitles taken to be good are cut in
// synthetic ways (a stretch removed, a block of lines from another file inserted, both, a frame-rate change on top, a
// block before the start), then fitted against the video's cached full transcript; and subtitles are paired with other
// videos (which must be rejected). Reads only what it is given; writes only under --out.
//
//   sections --manifest M.json --cache DIR --out DIR [--model NAME] [--seed 1] [--variants 3] [--cross true]
//            [--penalty P] [--min-jump S] [--min-seconds S] [--min-section N] [--agreement S] [--min-share F]
//            [--min-anchors N] [--min-removed S] [--keep-cost F] (defaults: the plugin's PiecewiseOptions)
//            [--debug "ID KIND"] [--profile] [--from S --to S] (print one case's lines, offsets a minute at a time, anchors)
//
// Manifest groups used: "good" (synthetic cuts and untouched), "wrong" (another episode's subtitle: must be rejected),
// "case" (fitted and reported as is, e.g. subtitles an audio check left unclear). Each option may be a comma list: every
// combination is run and summarised.
using System.Globalization;
using System.Text;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.SpeechToText;
using Jellyfin.Plugin.Subtitles.Sync;

namespace DiscrepancyEval;

public static class Sections
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // 25 fps subtitle on a 23.976 fps video: audio time = ratio × subtitle time
    private static readonly double Pal = 25 / (24000 / 1001.0);

    private sealed record Truth(string Kind, double Scale, List<(double At, double Offset)> Sections, HashSet<int> Flagged)
    {
        // Where each section starts in the (modified) file's time
        public List<(double At, double Offset)> FileSections { get; init; } = [];
    }

    private sealed record Case(string Id, string Group, string Kind, SubtitleDocument Doc, List<TranscribedWord> Words, double Duration, Truth? Truth);

    public static int Run(Dictionary<string, string> a)
    {
        var cache = Program.Get(a, "cache");
        var outDir = Program.Get(a, "out");
        Directory.CreateDirectory(outDir);
        var model = Program.Get(a, "model", string.Empty);
        var seed = int.Parse(Program.Get(a, "seed", "1"), Inv);
        var variants = int.Parse(Program.Get(a, "variants", "3"), Inv);
        var cross = bool.Parse(Program.Get(a, "cross", "true"));

        // Transcripts and subtitles
        var entries = Program.Manifest(a);
        var transcripts = new Dictionary<string, (List<TranscribedWord> Words, double Duration)>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (Program.Load(Program.CacheFile(cache, e, model)) is { } t)
            {
                transcripts[e.Id] = (t.Texts.Select((w, i) => new TranscribedWord(w, t.Times[i][0], t.Times[i][1], t.Times[i][2] < 0 ? null : t.Times[i][2])).ToList(), t.DurationSeconds);
            }
            else
            {
                Console.Error.WriteLine($"{e.Id}: no transcript, skipped");
            }
        }

        var good = entries.Where(e => e.Group == "good" && transcripts.ContainsKey(e.Id)).ToList();
        var docs = entries.Where(e => transcripts.ContainsKey(e.Id)).ToDictionary(e => e.Id, e => Program.Read(e.Subtitle), StringComparer.Ordinal);

        // Baselines: each good subtitle's own fit, so synthetic truths are relative to where it already sits
        var baseOptions = (Name: "defaults", Options: new PiecewiseOptions());
        var baseline = new Dictionary<string, (double Scale, double Offset)>(StringComparer.Ordinal);
        foreach (var e in good)
        {
            var fit = PiecewiseAligner.Fit(docs[e.Id], transcripts[e.Id].Words, transcripts[e.Id].Duration, baseOptions.Options);
            if (fit.Status == PiecewiseStatus.OneTiming)
            {
                baseline[e.Id] = (fit.Scale, fit.Sections[0].Offset);
            }
        }

        var cases = new List<Case>();
        var rng = new Random(seed);
        foreach (var e in good)
        {
            var (words, duration) = transcripts[e.Id];
            var doc = docs[e.Id];
            cases.Add(new Case(e.Id, "good", "untouched", doc, words, duration, baseline.TryGetValue(e.Id, out var b0) ? new Truth("untouched", b0.Scale, [(0, b0.Offset)], []) : null));
            if (!baseline.TryGetValue(e.Id, out var b) || Math.Abs(b.Scale - 1) > 1e-9)
            {
                continue;
            }

            var donors = good.Where(x => x.Id != e.Id).ToList();
            for (var v = 0; v < variants; v++)
            {
                var donor = docs[donors[rng.Next(donors.Count)].Id];
                cases.Add(Remove(e.Id, doc, words, duration, b.Offset, rng, 1, v));
                cases.Add(Insert(e.Id, doc, donor, words, duration, b.Offset, rng, v));
                cases.Add(Both(e.Id, doc, donor, words, duration, b.Offset, rng, v));
                cases.Add(Remove(e.Id, doc, words, duration, b.Offset, rng, Pal, v));
                if (v == 0)
                {
                    cases.Add(ColdOpen(e.Id, doc, donor, words, duration, b.Offset, rng));
                }
            }
        }

        foreach (var e in entries.Where(e => e.Group is "wrong" or "case" && transcripts.ContainsKey(e.Id)))
        {
            cases.Add(new Case(e.Id, e.Group, e.Group, docs[e.Id], transcripts[e.Id].Words, transcripts[e.Id].Duration, null));
        }

        if (cross)
        {
            // Every good subtitle against every other good video: never a fit
            foreach (var s in good)
            {
                foreach (var v in good.Where(x => x.Id != s.Id && x.Video != s.Video))
                {
                    cases.Add(new Case(s.Id + "@" + v.Id, "cross", "cross", docs[s.Id], transcripts[v.Id].Words, transcripts[v.Id].Duration, null));
                }
            }
        }

        var allRows = new StringBuilder("config,id,group,kind,status,scale,sections,true_sections,anchors,agreeing,boundary_err,offset_err,flag_tp,flag_fp,flag_fn,lines_ok,lines,explanation\n");
        var allSummary = new StringBuilder("config,cases_cut,count_ok,boundary_median,boundary_p90,boundary_max,offset_median,offset_max,lines_ok_share,flag_recall,flag_precision,untouched,false_segmented,false_rejected,wrong,wrong_accepted,cross,cross_accepted\n");
        var results = new System.Collections.Concurrent.ConcurrentDictionary<int, (string Rows, string Summary)>();
        var configs = Options(a).ToList();
        Parallel.For(0, configs.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, ci =>
        {
            var (name, options) = configs[ci];
            var rows = new StringBuilder();
            var summary = new StringBuilder();
            var boundaryErrors = new List<double>();
            var offsetErrors = new List<double>();
            int cut = 0, countOk = 0, linesOk = 0, lines = 0, tp = 0, fp = 0, fn = 0;
            int untouched = 0, falseSeg = 0, falseRej = 0, wrong = 0, wrongAcc = 0, crossN = 0, crossAcc = 0;
            foreach (var c in cases)
            {
                var fit = PiecewiseAligner.Fit(c.Doc, c.Words, c.Duration, options);
                if (a.TryGetValue("debug", out var dbg) && (c.Id + " " + c.Kind).StartsWith(dbg, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(c.Id + " " + c.Kind + ": " + fit.Explanation);
                    var lo = double.Parse(Program.Get(a, "from", "0"), Inv);
                    var hi = double.Parse(Program.Get(a, "to", "0"), Inv);
                    var all = TranscriptAligner.Anchors(c.Doc, [(0, new Transcript(c.Words, null, "x", "x", 0))]).OrderBy(x => x.SubtitleTime).ToList();
                    foreach (var an in all.Where(x => x.SubtitleTime >= lo && x.SubtitleTime <= hi))
                    {
                        Console.Error.WriteLine(string.Create(Inv, $"  anchor {an.SubtitleTime:0.00} -> {an.AudioTime:0.00} ({an.AudioTime - (fit.Scale * an.SubtitleTime):+0.00;-0.00})"));
                    }

                    // Rolling median offset at the same rate, a minute at a time
                    if (a.ContainsKey("profile"))
                    {
                        for (var m = 0.0; m < (all.Count == 0 ? 0 : all[^1].SubtitleTime); m += 30)
                        {
                            var o = all.Where(x => x.SubtitleTime >= m && x.SubtitleTime < m + 30).Select(x => x.AudioTime - x.SubtitleTime).Order().ToList();
                            var near = o.Count == 0 ? double.NaN : o[o.Count / 2];
                            var agree = o.Count(x => Math.Abs(x - near) <= 0.5);
                            Console.Error.WriteLine(string.Create(Inv, $"  {m / 60,5:0.0} min: {o.Count,3} anchors, median {near:+0.00;-0.00}, {agree} within 0.5 s"));
                        }
                    }
                    var flaggedSet = fit.NotInVideo.ToHashSet();
                    for (var i = 0; i < c.Doc.Cues.Count; i++)
                    {
                        var cue = c.Doc.Cues[i];
                        var sec = fit.Sections.Count > 0 ? PiecewiseFit.SectionAt(fit.Sections, cue.Start.TotalSeconds) : null;
                        var trueFlag = c.Truth?.Flagged.Contains(i) == true;
                        if (flaggedSet.Contains(i) || trueFlag || fit.Sections.Skip(1).Any(x => Math.Abs(x.From - cue.Start.TotalSeconds) < 60))
                        {
                            Console.Error.WriteLine(string.Create(Inv, $"  {i,5} {cue.Start.TotalSeconds,8:0.00}-{cue.End.TotalSeconds,8:0.00} sec{fit.Sections.ToList().IndexOf(sec!)} {(flaggedSet.Contains(i) ? "FLAG" : "    ")} {(trueFlag ? "TRUE" : "    ")} {cue.Text.Replace('\n', ' ')}"));
                        }
                    }
                }

                double? bErr = null, oErr = null;
                int ctp = 0, cfp = 0, cfn = 0, cok = 0, cn = 0;
                switch (c.Group)
                {
                    case "cross":
                        crossN++;
                        crossAcc += fit.Status == PiecewiseStatus.Rejected ? 0 : 1;
                        break;
                    case "wrong":
                        wrong++;
                        wrongAcc += fit.Status == PiecewiseStatus.Rejected ? 0 : 1;
                        break;
                    case "good" when c.Kind == "untouched":
                        untouched++;
                        falseSeg += fit.Status == PiecewiseStatus.Sections ? 1 : 0;
                        falseRej += fit.Status == PiecewiseStatus.Rejected ? 1 : 0;
                        break;
                    case "good" when c.Truth is { } t:
                        cut++;
                        if (fit.Status != PiecewiseStatus.Rejected && fit.Sections.Count == t.Sections.Count && Math.Abs(fit.Scale - t.Scale) < 1e-6)
                        {
                            countOk++;
                        }

                        // Jumps: each true one against the nearest found (video time)
                        var trueJumps = t.Sections.Skip(1).Select(s => s.At).ToList();
                        foreach (var j in trueJumps)
                        {
                            var err = fit.JumpsAt.Count == 0 ? double.PositiveInfinity : fit.JumpsAt.Min(x => Math.Abs(x - j));
                            boundaryErrors.Add(err);
                            bErr = Math.Max(bErr ?? 0, err);
                        }

                        if (fit.Status != PiecewiseStatus.Rejected && fit.Sections.Count == t.Sections.Count)
                        {
                            for (var k = 0; k < t.Sections.Count; k++)
                            {
                                var err = Math.Abs(fit.Sections[k].Offset - t.Sections[k].Offset);
                                offsetErrors.Add(err);
                                oErr = Math.Max(oErr ?? 0, err);
                            }
                        }

                        // Lines: flagged against the truth, the rest placed within 1 s of where they belong
                        var flagged = fit.NotInVideo.ToHashSet();
                        ctp = flagged.Count(t.Flagged.Contains);
                        cfp = flagged.Count(x => !t.Flagged.Contains(x));
                        cfn = t.Flagged.Count(x => !flagged.Contains(x));
                        tp += ctp;
                        fp += cfp;
                        fn += cfn;
                        if (fit.Status != PiecewiseStatus.Rejected)
                        {
                            for (var i = 0; i < c.Doc.Cues.Count; i++)
                            {
                                if (t.Flagged.Contains(i))
                                {
                                    continue;
                                }

                                cn++;
                                var start = c.Doc.Cues[i].Start.TotalSeconds;
                                var want = (t.Scale * start) + TrueOffset(t, start);
                                var got = (fit.Scale * start) + PiecewiseFit.SectionAt(fit.Sections, start).Offset;
                                cok += Math.Abs(want - got) <= 1 && !flagged.Contains(i) ? 1 : 0;
                            }
                        }
                        else
                        {
                            cn = c.Doc.Cues.Count - t.Flagged.Count;
                        }

                        linesOk += cok;
                        lines += cn;
                        break;
                }

                rows.Append(string.Join(',', name, c.Id, c.Group, c.Kind, fit.Status, fit.Scale.ToString("0.#####", Inv), fit.Sections.Count, c.Truth?.Sections.Count.ToString(Inv) ?? string.Empty,
                    fit.Anchors, fit.Agreeing, bErr?.ToString("0.##", Inv) ?? string.Empty, oErr?.ToString("0.###", Inv) ?? string.Empty, ctp, cfp, cfn, cok, cn, "\"" + fit.Explanation.Replace("\"", "'", StringComparison.Ordinal) + "\"")).Append('\n');
            }

            summary.Append(string.Join(',', name, cut, countOk, Q(boundaryErrors, 0.5), Q(boundaryErrors, 0.9), Q(boundaryErrors, 1), Q(offsetErrors, 0.5), Q(offsetErrors, 1),
                lines == 0 ? string.Empty : (linesOk / (double)lines).ToString("0.###", Inv),
                tp + fn == 0 ? string.Empty : (tp / (double)(tp + fn)).ToString("0.###", Inv),
                tp + fp == 0 ? string.Empty : (tp / (double)(tp + fp)).ToString("0.###", Inv),
                untouched, falseSeg, falseRej, wrong, wrongAcc, crossN, crossAcc)).Append('\n');
            Console.Error.WriteLine($"{name}: done");
            results[ci] = (rows.ToString(), summary.ToString());
        });
        foreach (var ci in results.Keys.Order())
        {
            allRows.Append(results[ci].Rows);
            allSummary.Append(results[ci].Summary);
        }

        File.WriteAllText(Path.Combine(outDir, "sections_cases.csv"), allRows.ToString());
        File.WriteAllText(Path.Combine(outDir, "sections_summary.csv"), allSummary.ToString());
        Console.WriteLine(allSummary.ToString());
        return 0;
    }

    private static string Q(List<double> values, double q)
    {
        if (values.Count == 0)
        {
            return string.Empty;
        }

        var v = values.Order().ToList();
        return v[Math.Min(v.Count - 1, (int)Math.Floor(q * (v.Count - 1)))].ToString("0.###", Inv);
    }

    // The true offset of a (modified-file) time: the last section starting at or before it (sections in file time)
    private static double TrueOffset(Truth t, double fileTime)
    {
        var o = t.Sections[0].Offset;
        foreach (var (at, offset) in t.FileSections)
        {
            if (at <= fileTime)
            {
                o = offset;
            }
        }

        return o;
    }

    private static IEnumerable<(string Name, PiecewiseOptions Options)> Options(Dictionary<string, string> a)
    {
        // Defaults: the plugin's own
        var d = new PiecewiseOptions();
        string D(double v) => v.ToString(Inv);
        foreach (var penalty in Program.Doubles(Program.Get(a, "penalty", D(d.SwitchPenalty))))
        foreach (var jump in Program.Doubles(Program.Get(a, "min-jump", D(d.MinJump))))
        foreach (var seconds in Program.Doubles(Program.Get(a, "min-seconds", D(d.MinSectionSeconds))))
        foreach (var section in Program.Doubles(Program.Get(a, "min-section", D(d.MinSectionAnchors))))
        foreach (var agreement in Program.Doubles(Program.Get(a, "agreement", D(d.Agreement))))
        foreach (var share in Program.Doubles(Program.Get(a, "min-share", D(d.MinShare))))
        foreach (var anchors in Program.Doubles(Program.Get(a, "min-anchors", D(d.MinAnchors))))
        foreach (var removed in Program.Doubles(Program.Get(a, "min-removed", D(d.MinRemoved))))
        foreach (var keep in Program.Doubles(Program.Get(a, "keep-cost", D(d.KeepCost))))
        {
            yield return (string.Create(Inv, $"p{penalty}_j{jump}_s{seconds}_n{section}_a{agreement}_sh{share}_m{anchors}_r{removed}_k{keep}"), d with
            {
                SwitchPenalty = penalty, MinJump = jump, MinSectionSeconds = seconds, MinSectionAnchors = (int)section, Agreement = agreement,
                MinShare = share, MinSectionShare = share, MinAnchors = (int)anchors, MinRemoved = removed, KeepCost = keep,
            });
        }
    }

    // A stretch the subtitle doesn't have: lines in [T, T+L) dropped, later ones moved L earlier (the video has more);
    // with a frame-rate ratio, every time is then divided by it (a subtitle timed for the other speed)
    private static Case Remove(string id, SubtitleDocument doc, List<TranscribedWord> words, double duration, double offset, Random rng, double ratio, int v)
    {
        var (t, l) = Pick(doc, rng, 30, 120);
        var cues = new List<SubtitleCue>();
        var firstAfter = double.NaN;
        foreach (var c in doc.Cues)
        {
            var s = c.Start.TotalSeconds;
            if (s >= t && s < t + l)
            {
                continue;
            }

            if (s >= t + l)
            {
                if (double.IsNaN(firstAfter))
                {
                    firstAfter = s;
                }

                cues.Add(Shift(c, -l));
            }
            else
            {
                cues.Add(c);
            }
        }

        // The jump shows in the video where the first line after it is heard: its own time, as the video has everything
        cues = [.. cues.Select(c => Scale(c, 1 / ratio))];
        var truth = new Truth(ratio == 1 ? "remove" : "remove+fps", ratio, [(0, offset), ((firstAfter * 1) + offset, offset + l)], []) { FileSections = [(0, offset), ((t / ratio) + 0.001, offset + l)] };
        return new Case(id, "good", truth.Kind + "#" + v.ToString(Inv) + string.Create(Inv, $"@{t:0}+{l:0}"), doc with { Cues = cues }, words, duration, truth);
    }

    // A block the video doesn't have: lines from another subtitle put in [T, T+L), later lines moved L later
    private static Case Insert(string id, SubtitleDocument doc, SubtitleDocument donor, List<TranscribedWord> words, double duration, double offset, Random rng, int v)
    {
        var (t, l) = Pick(doc, rng, 30, 120);
        var (cues, flagged, firstAfter) = WithBlock(doc, donor, t, l, rng);
        var truth = new Truth("insert", 1, [(0, offset), (firstAfter + offset, offset - l)], flagged) { FileSections = [(0, offset), (t, offset - l)] };
        return new Case(id, "good", "insert#" + v.ToString(Inv) + string.Create(Inv, $"@{t:0}+{l:0}"), doc with { Cues = cues }, words, duration, truth);
    }

    // A stretch removed, then (later) a block inserted
    private static Case Both(string id, SubtitleDocument doc, SubtitleDocument donor, List<TranscribedWord> words, double duration, double offset, Random rng, int v)
    {
        var span = doc.Cues[^1].Start.TotalSeconds;
        var t1 = span * (0.15 + (rng.NextDouble() * 0.25));
        var l1 = 30 + (rng.NextDouble() * 90);
        var t2 = span * (0.6 + (rng.NextDouble() * 0.25));
        var l2 = 30 + (rng.NextDouble() * 90);

        // Removal first (on the original times)
        var removed = new List<SubtitleCue>();
        var firstAfter1 = double.NaN;
        foreach (var c in doc.Cues)
        {
            var s = c.Start.TotalSeconds;
            if (s >= t1 && s < t1 + l1)
            {
                continue;
            }

            if (s >= t1 + l1 && double.IsNaN(firstAfter1))
            {
                firstAfter1 = s;
            }

            removed.Add(s >= t1 + l1 ? Shift(c, -l1) : c);
        }

        // Insertion at t2 on the new clock (original time t2 + l1)
        var (cues, flagged, firstAfter2) = WithBlock(doc with { Cues = removed }, donor, t2, l2, rng);
        var truth = new Truth("both", 1, [(0, offset), (firstAfter1 + offset, offset + l1), (firstAfter2 + l1 + offset, offset + l1 - l2)], flagged)
        {
            FileSections = [(0, offset), (t1 + 0.001, offset + l1), (t2, offset + l1 - l2)],
        };
        return new Case(id, "good", "both#" + v.ToString(Inv) + string.Create(Inv, $"@{t1:0}-{l1:0};{t2:0}+{l2:0}"), doc with { Cues = cues }, words, duration, truth);
    }

    // A block before the start (a recap or cold open the video doesn't have)
    private static Case ColdOpen(string id, SubtitleDocument doc, SubtitleDocument donor, List<TranscribedWord> words, double duration, double offset, Random rng)
    {
        var l = 30 + (rng.NextDouble() * 60);
        var (cues, flagged, _) = WithBlock(doc, donor, 0, l, rng);
        var truth = new Truth("coldopen", 1, [(0, offset - l)], flagged) { FileSections = [(0, offset - l)] };
        return new Case(id, "good", string.Create(Inv, $"coldopen@0+{l:0}"), doc with { Cues = cues }, words, duration, truth);
    }

    private static (List<SubtitleCue> Cues, HashSet<int> Flagged, double FirstAfter) WithBlock(SubtitleDocument doc, SubtitleDocument donor, double t, double l, Random rng)
    {
        // The donor's lines from a random point, fitted into the block with a short pause each side
        var from = donor.Cues[rng.Next(donor.Cues.Count / 4, donor.Cues.Count * 3 / 4)].Start.TotalSeconds;
        var block = donor.Cues.Where(c => c.Start.TotalSeconds >= from && c.End.TotalSeconds - from <= l - 1).Select(c => Shift(c, t + 0.5 - from)).ToList();
        var firstAfter = doc.Cues.FirstOrDefault(c => c.Start.TotalSeconds >= t)?.Start.TotalSeconds ?? t;
        var before = doc.Cues.Where(c => c.Start.TotalSeconds < t).Select(c => c.End.TotalSeconds > t ? c with { End = TimeSpan.FromSeconds(t) } : c);
        var after = doc.Cues.Where(c => c.Start.TotalSeconds >= t).Select(c => Shift(c, l));
        var all = before.Concat(block).Concat(after).OrderBy(c => c.Start).ToList();
        var flagged = new HashSet<int>();
        for (var i = 0; i < all.Count; i++)
        {
            if (block.Contains(all[i]))
            {
                flagged.Add(i);
            }
        }

        return (all, flagged, firstAfter);
    }

    // A place for a cut (between 15 % and 85 % of the subtitle) and a length
    private static (double T, double L) Pick(SubtitleDocument doc, Random rng, double min, double max)
    {
        var span = doc.Cues[^1].Start.TotalSeconds;
        return (span * (0.15 + (rng.NextDouble() * 0.7)), min + (rng.NextDouble() * (max - min)));
    }

    private static SubtitleCue Shift(SubtitleCue c, double by)
        => c with { Start = TimeSpan.FromSeconds(Math.Max(0, c.Start.TotalSeconds + by)), End = TimeSpan.FromSeconds(Math.Max(0, c.End.TotalSeconds + by)) };

    private static SubtitleCue Scale(SubtitleCue c, double by)
        => c with { Start = TimeSpan.FromSeconds(c.Start.TotalSeconds * by), End = TimeSpan.FromSeconds(c.End.TotalSeconds * by) };
}
