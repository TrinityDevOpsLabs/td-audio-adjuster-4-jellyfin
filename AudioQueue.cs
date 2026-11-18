using System.Threading.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AudioAdjuster4Jellyfin;

public sealed class AudioQueue(IMediaEncoder encoder, ILibraryMonitor monitor, ILogger<AudioQueue> logger)
    : BackgroundService
{
    private readonly Channel<(Guid Id, string Path, AudioSettings Settings)> _pending =
        Channel.CreateBounded<(Guid, string, AudioSettings)>(32);
    private readonly Dictionary<Guid, Job> _jobs = new();
    private readonly HashSet<string> _activePaths = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _gate = new();

    public sealed record Job(Guid Id, string Name, string State, string? Message, DateTimeOffset Updated,
        string? Phase = null, int? Percent = null);

    public Job[] GetJobs()
    {
        lock (_gate)
            return _jobs.Values.OrderByDescending(job => job.Updated).ToArray();
    }

    public bool IsActive(Guid id)
    {
        lock (_gate)
            return _jobs.TryGetValue(id, out var job) && job.State is "Queued" or "Running";
    }

    public bool TrySubmit(Guid id, string name, string path, AudioSettings settings)
    {
        lock (_gate)
        {
            if (IsActive(id))
                return false;
            if (_activePaths.Contains(path) || !_pending.Writer.TryWrite((id, path, settings)))
                return false;

            _activePaths.Add(path);
            _jobs[id] = new(id, name, "Queued", null, DateTimeOffset.UtcNow);
            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (id, path, settings) in _pending.Reader.ReadAllAsync(stoppingToken))
        {
            lock (_gate)
                _jobs[id] = _jobs[id] with { State = "Running", Phase = "Preparing movie", Updated = DateTimeOffset.UtcNow };

            string? error = null;
            try
            {
                await AudioProcessor.AdjustAsync(encoder.EncoderPath, encoder.ProbePath, path, settings, stoppingToken, (phase, percent) =>
                {
                    lock (_gate)
                        _jobs[id] = _jobs[id] with { Phase = phase, Percent = percent };
                });
                try
                {
                    monitor.ReportFileSystemChanged(path);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not notify Jellyfin of the updated movie {ItemId}", id);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audio adjustment failed for movie {ItemId}", id);
                error = ex.Message;
            }
            finally
            {
                lock (_gate)
                {
                    _activePaths.Remove(path);
                    _jobs[id] = _jobs[id] with
                    {
                        State = stoppingToken.IsCancellationRequested ? "Stopped" : error is null ? "Completed" : "Failed",
                        Message = error,
                        Phase = null,
                        Percent = null,
                        Updated = DateTimeOffset.UtcNow
                    };

                    foreach (var old in _jobs.Values.Where(job => job.State is not ("Queued" or "Running"))
                        .OrderByDescending(job => job.Updated).Skip(25).ToArray())
                        _jobs.Remove(old.Id);
                }
            }
        }
    }
}
