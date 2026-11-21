using System.Diagnostics;
using System.Text.Json;

namespace AudioAdjuster4Jellyfin.Tests;

public sealed class AudioProcessorTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("audio-adjuster-test-").FullName;
    internal static string Encoder => Environment.GetEnvironmentVariable("TEST_FFMPEG") ?? "ffmpeg";
    internal static string Probe => Environment.GetEnvironmentVariable("TEST_FFPROBE") ?? "ffprobe";

    [Theory]
    [InlineData("mkv", "Normalize", "Replace", 1, "Original integrated loudness")]
    [InlineData("mp4", "Percentage", "Append", 2, "Keeping 80%")]
    [InlineData("mkv", "Decibels", "Append", 2, "Volume change -2 dB")]
    public async Task AdjustsAudioAndPreservesOriginal(string extension, string mode, string outputMode, int streams, string detail)
    {
        var path = await CreateMovieAsync(_directory, extension);
        var original = await File.ReadAllBytesAsync(path);
        var settings = new AudioSettings { Mode = mode, OutputMode = outputMode };
        var details = new List<string>();
        var debug = new List<string>();
        var phases = new List<string>();
        await AudioProcessor.AdjustAsync(Encoder, Probe, path, settings, CancellationToken.None,
            (phase, _) => phases.Add(phase), details.Add, debug.Add);
        Assert.Contains(detail, Assert.Single(details));
        Assert.Contains(debug, line => line.Contains("Selected audio stream"));
        Assert.Contains(debug, line => line.Contains("exited with code 0"));
        Assert.Contains("Saving original backup", phases);
        Assert.Equal(original, await File.ReadAllBytesAsync(path + ".audioadjuster-original"));

        await AudioProcessor.AdjustAsync(Encoder, Probe, path, settings, CancellationToken.None, (_, _) => { });
        var report = await RunAsync(Probe, "-v", "error", "-select_streams", "a", "-show_streams", "-of", "json", path);
        using var json = JsonDocument.Parse(report);
        Assert.Equal(streams, json.RootElement.GetProperty("streams").GetArrayLength());
        Assert.Empty(Directory.GetFiles(_directory, ".audioadjuster-*"));
    }

    [Fact]
    public async Task FailedEncoderLeavesOriginalAndCleansTemporaryFiles()
    {
        var path = await CreateMovieAsync(_directory);
        var original = await File.ReadAllBytesAsync(path);
        var debug = new List<string>();
        await Assert.ThrowsAsync<IOException>(() => AudioProcessor.AdjustAsync("/bin/false", Probe, path,
            new AudioSettings(), CancellationToken.None, (_, _) => { }, logDebug: debug.Add));
        Assert.Contains(debug, line => line.Contains("exited with code 1"));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CancellationDuringProbeLeavesOriginalIntact()
    {
        var path = await CreateMovieAsync(_directory);
        var original = await File.ReadAllBytesAsync(path);
        var slowProbe = Path.Combine(_directory, "probe");
        await File.WriteAllTextAsync(slowProbe, "#!/bin/sh\nexec sleep 30\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(slowProbe, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        using var cancellation = new CancellationTokenSource();
        var debug = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AudioProcessor.AdjustAsync(Encoder, slowProbe, path,
            new AudioSettings(), cancellation.Token, (_, _) => { }, logDebug: detail =>
            {
                debug.Add(detail);
                if (detail == "Probe audio streams started")
                    cancellation.Cancel();
            }));
        Assert.Contains(debug, line => line.Contains("canceled after"));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_directory, ".audioadjuster-*"));
    }

    [Fact]
    public async Task ConcurrentFileChangeIsPreserved()
    {
        var path = await CreateMovieAsync(_directory);
        await Assert.ThrowsAsync<IOException>(() => AudioProcessor.AdjustAsync(Encoder, Probe, path,
            new AudioSettings { KeepBackup = false }, CancellationToken.None, (phase, _) =>
            {
                if (phase == "Finishing")
                    File.WriteAllText(path, "changed while processing");
            }));
        Assert.Equal("changed while processing", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    internal static async Task<string> CreateMovieAsync(string directory, string extension = "mkv")
    {
        var path = Path.Combine(directory, $"sample.{extension}");
        await RunAsync(Encoder, "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=32x32:rate=5:duration=3",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:v", "mpeg4", "-c:a", "aac", path);
        return path;
    }

    private static async Task<string> RunAsync(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
