using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Common.Resilience;

namespace Jellyfin.Plugin.Subtitles.SpeechToText.BuiltIn;

/// <summary>
/// The built-in speech-to-text: whisper.cpp's command-line program, run on this server's CPU. The program and model are
/// fetched and checked by <see cref="BuiltInInstaller"/> on first use (only once the administrator has allowed it) and
/// checked again before every run. Each run is bounded: low priority, a capped thread count, a time limit, and the whole
/// process tree is killed on cancel or timeout. Arguments are passed as a list (no shell) and every path is absolute.
/// </summary>
public sealed class BuiltInSpeechToText : ISpeechToText
{
    /// <summary>The largest output file read.</summary>
    public const int MaxOutputBytes = 20 * 1024 * 1024;

    private readonly BuiltInInstaller _installer;
    private readonly string _workFolder;
    private readonly string _platform;
    private readonly string _model;

    /// <summary>
    /// Initializes a new instance of the <see cref="BuiltInSpeechToText"/> class.
    /// </summary>
    /// <param name="installer">Installer (shared).</param>
    /// <param name="workFolder">Folder for temporary audio and output.</param>
    /// <param name="platform">Platform (see <see cref="BuiltInSource.CurrentPlatform"/>).</param>
    /// <param name="model">Model setting value (<c>base</c> or <c>small</c>).</param>
    public BuiltInSpeechToText(BuiltInInstaller installer, string workFolder, string platform, string model)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        ArgumentException.ThrowIfNullOrWhiteSpace(workFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _workFolder = Path.GetFullPath(workFolder);
        _platform = platform;
        _model = model;
    }

    /// <inheritdoc />
    public string Id => SpeechToTextFactory.BuiltIn;

    /// <summary>
    /// Gets the threads a run may use: half the CPUs, at most 8, so playback and transcoding keep priority.
    /// </summary>
    public static int Threads => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    /// <summary>
    /// The time limit for a run: generous for a slow CPU, but a stuck run always ends.
    /// </summary>
    /// <param name="audioSeconds">Length of the audio.</param>
    /// <returns>The limit.</returns>
    public static TimeSpan TimeLimit(double audioSeconds) => TimeSpan.FromSeconds(Math.Clamp(120 + (audioSeconds * 5), 120, 3 * 3600));

    /// <inheritdoc />
    public async Task<Transcript> TranscribeAsync(float[] samples, string? language, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var (program, model) = await _installer.EnsureAsync(_platform, _model, cancellationToken).ConfigureAwait(false);
        var audioSeconds = samples.Length / (double)Audio.AudioFormat.SampleRate;

        var work = Path.Combine(_workFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        try
        {
            var wav = Path.Combine(work, "audio.wav");
            await File.WriteAllBytesAsync(wav, WavEncoder.Encode(samples), cancellationToken).ConfigureAwait(false);
            var output = Path.Combine(work, "result");
            var messages = await RunAsync(program, Arguments(model, wav, output, language), audioSeconds, cancellationToken).ConfigureAwait(false);

            // whisper.cpp can end with success without writing anything (e.g. audio it couldn't read)
            var json = output + ".json";
            if (!File.Exists(json) || new FileInfo(json).Length > MaxOutputBytes)
            {
                throw new SpeechToTextException("The built-in speech-to-text produced no usable result: " + messages) { Failure = FailureClass.Transient };
            }

            try
            {
                var (words, detected) = WhisperCppOutput.Parse(await File.ReadAllTextAsync(json, Encoding.UTF8, cancellationToken).ConfigureAwait(false));
                return new Transcript(words, Languages.ToTwoLetter(detected) ?? language, Id, _model, audioSeconds);
            }
            catch (JsonException ex)
            {
                throw new SpeechToTextException("The built-in speech-to-text's result couldn't be read.", ex) { Failure = FailureClass.Transient };
            }
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Removed with the next cleanup of the work folder
            }
        }
    }

    /// <summary>
    /// The program's arguments.
    /// </summary>
    /// <param name="model">Absolute model path.</param>
    /// <param name="wav">Absolute audio path.</param>
    /// <param name="output">Absolute output path without extension.</param>
    /// <param name="language">Two-letter language, or <c>null</c> to detect it.</param>
    /// <returns>The arguments, one per item.</returns>
    internal string[] Arguments(string model, string wav, string output, string? language)
    {
        var lang = Languages.ToTwoLetter(language) ?? "auto";
        return
        [
            "--model", Path.GetFullPath(model),
            "--file", Path.GetFullPath(wav),
            "--output-file", Path.GetFullPath(output),
            "--output-json-full",
            "--no-prints",
            "--language", lang,
            "--threads", Threads.ToString(CultureInfo.InvariantCulture),
            // Word timings from the attention heads (DTW) are steadier than token timestamps; DTW needs flash attention off
            "--no-flash-attn",
            "--dtw", _model,
        ];
    }

    // Runs the program; returns the end of what it wrote to stderr (its own messages; never media content)
    private static async Task<string> RunAsync(string program, string[] arguments, double audioSeconds, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(program)!,
        };
        foreach (var a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = start };
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            lock (errors)
            {
                    if (e.Data is { } line)
                {
                    errors.AppendLine(line);
                    if (errors.Length > 8000)
                    {
                        errors.Remove(0, errors.Length - 4000);
                    }
                }
            }
        };
        process.OutputDataReceived += (_, _) => { };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            throw new SpeechToTextException("The built-in speech-to-text couldn't be started: " + ex.Message, ex) { Failure = FailureClass.BadRequest };
        }

        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // Lowering priority is best-effort
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeLimit(audioSeconds));
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already ended
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new SpeechToTextException("The built-in speech-to-text took too long and was stopped.") { Failure = FailureClass.Transient };
        }

        // (WaitForExitAsync also waits for the redirected output to be read to the end)
        string tail;
        lock (errors)
        {
            tail = errors.ToString().Trim();
        }

        tail = tail.Length > 300 ? tail[^300..] : tail;
        if (process.ExitCode != 0)
        {
            throw new SpeechToTextException(string.Create(CultureInfo.InvariantCulture, $"The built-in speech-to-text failed (exit code {process.ExitCode}): {tail}")) { Failure = FailureClass.Transient };
        }

        return tail;
    }
}
