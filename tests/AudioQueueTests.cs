using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AudioAdjuster4Jellyfin.Tests;

public sealed class AudioQueueTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("audio-queue-test-").FullName;

    [Fact]
    public void DeduplicatesCapsAndCancelsPendingAdjustments()
    {
        using var discord = CreateDiscord();
        using var queue = new AudioQueue(Mock.Of<IMediaEncoder>(), Mock.Of<ILibraryMonitor>(), NullLogger<AudioQueue>.Instance, discord);
        var id = Guid.NewGuid();
        var settings = new AudioSettings();
        Assert.True(queue.TrySubmit(id, "one", "path", settings));
        Assert.True(queue.IsActive(id));
        Assert.False(queue.TrySubmit(id, "one", "different", settings));
        Assert.False(queue.TrySubmit(Guid.NewGuid(), "duplicate path", "path", settings));
        for (var n = 1; n < 32; n++)
            Assert.True(queue.TrySubmit(Guid.NewGuid(), $"Movie {n}", $"path{n}", settings));

        Assert.False(queue.TrySubmit(Guid.NewGuid(), "overflow", "overflow", settings));
        Assert.False(queue.TryCancel(Guid.NewGuid()));
        Assert.True(queue.TryCancel(id));
        Assert.False(queue.TryCancel(id));
        Assert.False(queue.IsActive(id));
        Assert.Equal("Canceled", queue.GetJobs().Single(job => job.Id == id).State);
        Assert.True(queue.TrySubmit(id, "one", "path", settings));
    }

    [Fact]
    public async Task WorkerCompletesValidJobsAndContinuesAfterFailures()
    {
        var path = await AudioProcessorTests.CreateMovieAsync(_directory);
        var encoder = new Mock<IMediaEncoder>();
        encoder.SetupGet(x => x.EncoderPath).Returns(AudioProcessorTests.Encoder);
        encoder.SetupGet(x => x.ProbePath).Returns(AudioProcessorTests.Probe);
        var monitor = new Mock<ILibraryMonitor>();
        monitor.Setup(x => x.ReportFileSystemChanged(path)).Throws(new IOException("Monitor unavailable"));
        using var discord = CreateDiscord();
        using var queue = new AudioQueue(encoder.Object, monitor.Object, NullLogger<AudioQueue>.Instance, discord);
        var failed = Guid.NewGuid();
        var completed = Guid.NewGuid();
        Assert.True(queue.TrySubmit(failed, "missing", Path.Combine(_directory, "missing.mkv"), new AudioSettings()));
        Assert.True(queue.TrySubmit(completed, "sample", path, new AudioSettings { LoggingLevel = "Debug" }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await queue.StartAsync(timeout.Token);
        try
        {
            while (queue.IsActive(completed))
                await Task.Delay(20, timeout.Token);

            var jobs = queue.GetJobs();
            Assert.Equal("Completed", jobs.Single(job => job.Id == completed).State);
            var failure = jobs.Single(job => job.Id == failed);
            Assert.Equal("Failed", failure.State);
            Assert.Contains("supported local file", failure.Message);
            monitor.Verify(x => x.ReportFileSystemChanged(path), Times.Once);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }

    private static DiscordNotifications CreateDiscord() =>
        new(Mock.Of<ILibraryManager>(), NullLogger<DiscordNotifications>.Instance);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
