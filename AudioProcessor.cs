using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AudioAdjuster4Jellyfin;

internal static class AudioProcessor
{
    private const string TrackTitle = "Adjusted Audio (AudioAdjuster4Jellyfin)";
    private const string BackupSuffix = ".audioadjuster-original";

    public static bool Supports(string? path) =>
        path is not null && Path.IsPathFullyQualified(path)
        && Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mp4"
        && File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) == false;

    public static async Task AdjustAsync(string encoder, string probe, string path, AudioSettings settings,
        CancellationToken token, Action<string, int?> reportProgress)
    {
        if (!Supports(path))
            throw new IOException("The movie is no longer a supported local file.");

        var original = new FileInfo(path);
        var length = original.Length;
        var lastWrite = original.LastWriteTimeUtc;
        var backup = path + BackupSuffix;
        if (File.Exists(backup) && File.GetAttributes(backup).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The original backup is a symbolic link.");

        var hasBackup = File.Exists(backup);
        var source = hasBackup ? backup : path;
        var temporary = Path.Combine(original.DirectoryName!, $".audioadjuster-{Guid.NewGuid():N}.tmp");
        var stagedBackup = temporary + ".backup";
        var container = original.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? "mp4" : "matroska";
        var demuxer = container == "mp4" ? "mov" : "matroska";
        var append = settings.OutputMode == "Append";

        try
        {
            using var streams = JsonDocument.Parse(await RunAsync(probe,
                ["-v", "error", "-f", demuxer, "-select_streams", "a", "-show_streams", "-show_format", "-of", "json", source],
                token, isProbe: true));
            var audio = streams.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var originals = audio.Where(stream => !IsAdjusted(stream)).ToArray();
            if (originals.Length == 0)
                throw new IOException("The movie has no audio track to adjust.");

            var primary = originals[0].GetProperty("index").GetInt32();
            var duration = streams.RootElement.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var value)
                && double.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var seconds)
                && double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
            var filter = settings.Mode switch
            {
                "Normalize" => await MeasureAsync(encoder, source, demuxer, primary, settings.TargetLufs, token,
                    TrackProgress("Measuring loudness", duration, reportProgress)),
                "Decibels" => $"volume={Number(Clamp(settings.GainDb, -6, -1, -2))}dB",
                _ => $"volume={Number(Math.Clamp(settings.VolumePercent, 50, 90) / 100.0)}"
            };

            List<string> args = ["-f", demuxer, "-i", source, "-map", "0"];
            foreach (var stream in audio.Where(IsAdjusted))
                args.AddRange(["-map", $"-0:{stream.GetProperty("index").GetInt32()}"]);

            var track = append ? originals.Length : 0;
            if (append)
                args.AddRange(["-map", $"0:{primary}", $"-disposition:a:{track}", "0",
                    $"-metadata:s:a:{track}", $"title={TrackTitle}",
                    $"-metadata:s:a:{track}", $"handler_name={TrackTitle}"]);

            args.AddRange(["-c", "copy", $"-c:a:{track}", "aac", $"-filter:a:{track}", filter,
                $"-ar:a:{track}", "48000", "-f", container, temporary]);
            await RunAsync(encoder, args, token, progress: TrackProgress("Writing adjusted movie", duration, reportProgress));

            reportProgress(settings.KeepBackup && !hasBackup ? "Saving original backup" : "Finishing", null);
            if (settings.KeepBackup && !hasBackup)
            {
                await using var input = File.OpenRead(path);
                await using var output = new FileStream(stagedBackup, FileMode.CreateNew, FileAccess.Write);
                await input.CopyToAsync(output, token);
                reportProgress("Finishing", null);
            }

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                File.SetUnixFileMode(temporary, mode);
                if (settings.KeepBackup && !hasBackup)
                    File.SetUnixFileMode(stagedBackup, mode);
            }

            original.Refresh();
            if (!original.Exists || original.Length != length || original.LastWriteTimeUtc != lastWrite)
                throw new IOException("The movie changed during processing. Submit it again.");

            token.ThrowIfCancellationRequested();
            if (settings.KeepBackup && !hasBackup)
                File.Move(stagedBackup, backup);

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
            File.Delete(stagedBackup);
        }
    }

    private static Action<double> TrackProgress(string phase, double duration, Action<string, int?> report)
    {
        report(phase, duration > 0 ? 0 : null);
        var last = 0;
        return seconds =>
        {
            if (duration <= 0)
                return;

            var percent = (int)Math.Clamp(seconds / duration * 100, 0, 99);
            if (percent > last)
            {
                last = percent;
                report(phase, percent);
            }
        };
    }

    private static async Task<string> MeasureAsync(string encoder, string path, string demuxer, int streamIndex, double lufs,
        CancellationToken token, Action<double> progress)
    {
        var filter = $"loudnorm=I={Number(Clamp(lufs, -30, -14, -23))}:TP=-2:LRA=50";
        var report = await RunAsync(encoder,
            ["-f", demuxer, "-i", path, "-map", $"0:{streamIndex}", "-af", filter + ":print_format=json", "-f", "null", "-"],
            token, progress: progress);
        var start = report.LastIndexOf('{');
        var end = report.LastIndexOf('}');
        if (start < 0 || end < start)
            throw new IOException("FFmpeg did not report loudness measurements.");

        using var json = JsonDocument.Parse(report[start..(end + 1)]);
        var keys = new[] { ("input_i", "measured_I"), ("input_tp", "measured_TP"),
            ("input_lra", "measured_LRA"), ("input_thresh", "measured_thresh"), ("target_offset", "offset") };
        foreach (var (key, option) in keys)
        {
            if (!double.TryParse(json.RootElement.GetProperty(key).GetString(), CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value))
                throw new IOException("The first audio track has no measurable loudness.");

            filter += $":{option}={Number(value)}";
        }

        return filter + ":linear=true";
    }

    private static async Task<string> RunAsync(string encoder, IEnumerable<string> args, CancellationToken token,
        bool isProbe = false, Action<double>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(encoder)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = isProbe || progress is not null,
            CreateNoWindow = true
        };
        string[] common = isProbe ? ["-protocol_whitelist", "file,pipe"]
            : ["-hide_banner", "-nostdin", "-nostats", "-n", "-protocol_whitelist", "file,pipe"];
        if (progress is not null)
        {
            start.ArgumentList.Add("-progress");
            start.ArgumentList.Add("pipe:1");
        }
        foreach (var arg in common.Concat(args))
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new IOException("Could not start FFmpeg.");
        var stdout = isProbe ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
        var progressTask = progress is null ? Task.CompletedTask : ReadProgressAsync(process.StandardOutput, progress);
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }

            await process.WaitForExitAsync();
            await stderr;
            await stdout;
            await progressTask;
            throw;
        }

        var report = await stderr;
        await progressTask;
        if (process.ExitCode != 0)
            throw new IOException("FFmpeg failed. " + report[^Math.Min(report.Length, 1200)..].Trim());

        return isProbe ? await stdout : report;
    }

    private static async Task ReadProgressAsync(StreamReader output, Action<double> report)
    {
        const string prefix = "out_time_us=";
        while (await output.ReadLineAsync() is { } line)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(prefix.Length), CultureInfo.InvariantCulture, out var microseconds))
                report(microseconds / 1_000_000d);
        }
    }

    private static bool IsAdjusted(JsonElement stream) =>
        stream.TryGetProperty("tags", out var tags)
        && tags.EnumerateObject().Any(tag => tag.Name.ToLowerInvariant() is "title" or "handler_name"
            && tag.Value.GetString() == TrackTitle);

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);
}
