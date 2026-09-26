using System;
using System.IO;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Subtitles.Configuration;
using Jellyfin.Plugin.Subtitles.Formats;
using Jellyfin.Plugin.Subtitles.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.Subtitles.Tests;

public sealed class ReviewPass2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subs-pass2-" + Guid.NewGuid().ToString("N"));

    public ReviewPass2Tests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // SUB-04: downloads are capped while reading
    [Fact]
    public async Task An_endless_download_stops_just_past_the_limit()
    {
        var endless = new Endless();

        Assert.Null(await SubtitleReader.ReadLimitedAsync(endless, TestContext.Current.CancellationToken));
        Assert.InRange(endless.Served, SubtitleReader.MaxBytes, SubtitleReader.MaxBytes + (2 * 81920));

        using var small = new MemoryStream(new byte[1234]);
        Assert.Equal(1234, (await SubtitleReader.ReadLimitedAsync(small, TestContext.Current.CancellationToken))!.Length);
    }

    // SUB-04: a huge local file is recorded once and not read again until it changes
    [Fact]
    public async Task A_huge_local_file_is_marked_too_large_without_being_read()
    {
        var path = Path.Combine(_dir, "Film.en.srt");
        await using (var f = File.Create(path))
        {
            f.SetLength(SubtitleReader.MaxBytes + 1);
        }

        var processor = new SubtitleProcessor(new ResultStore(Path.Combine(_dir, "results.json")), new SubtitleFiles(Path.Combine(_dir, "originals")));
        var job = new SubtitleJob(Guid.NewGuid(), "Invented Film", Path.Combine(_dir, "Film.mkv"), path, "eng", TimeSpan.FromMinutes(90), 0);

        var result = await processor.ProcessAsync(job, null!, null, new Policies(ChangePolicy.Automatic, ChangePolicy.Review, new CleanupSettings()), TestContext.Current.CancellationToken);

        Assert.Equal(ResultStatus.TooLarge, result.Status);
        var fingerprint = await SubtitleFiles.FingerprintFileAsync(path, TestContext.Current.CancellationToken);
        Assert.StartsWith("large-", fingerprint, StringComparison.Ordinal);
        Assert.False(processor.NeedsCheck(path, fingerprint));

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        Assert.True(processor.NeedsCheck(path, await SubtitleFiles.FingerprintFileAsync(path, TestContext.Current.CancellationToken)));
    }

    // SUB-12: the provider's own allowance, or a failed sign-in, stops the run for today (classified: see SubDlTests)
    [Fact]
    public void Provider_limits_and_sign_in_failures_stop_the_run()
    {
        Assert.True(FindRules.StopsTheRun(Candidates.ProviderFailures.Classify(new RateLimitExceededException("OpenSubtitles download limit reached"))!));
        Assert.True(FindRules.StopsTheRun(Candidates.ProviderFailures.Classify(new AuthenticationException("login failed"))!));
        Assert.False(FindRules.StopsTheRun(new IOException("disk")));
    }

    // SUB-13: forced-only tracks never count; picture-based ones by setting
    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]
    public void Which_existing_tracks_count_as_having_subtitles(bool forced, bool text, bool countImages, bool counts)
        => Assert.Equal(counts, FindRules.Counts(forced, text, countImages));

    // Named like the OpenSubtitles plugin's exception, which is classified by name where it enters the plugin
    private sealed class RateLimitExceededException(string message) : Exception(message);

    private sealed class Endless : Stream
    {
        public long Served { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => Served; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Served += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
