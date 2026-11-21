using System.Threading.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AudioAdjuster4Jellyfin;

public sealed class AudioQueue(IMediaEncoder encoder, ILibraryMonitor monitor, ILogger<AudioQueue> logger,
    DiscordNotifications discord)
    : BackgroundService
{
    private readonly LinkedList<(Guid Id, string Path, AudioSettings Settings)> _pending = new();
    private readonly Channel<bool> _ready = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly Dictionary<Guid, Job> _jobs = new();
    private readonly HashSet<string> _activePaths = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _gate = new();
    private CancellationTokenSource? _runningCancellation;

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
            return _jobs.TryGetValue(id, out var job) && IsActive(job);
    }

    public bool TrySubmit(Guid id, string name, string path, AudioSettings settings)
    {
        lock (_gate)
        {
            if (IsActive(id))
                return false;
            if (_activePaths.Contains(path) || _pending.Count >= 32)
                return false;

            _pending.AddLast((id, path, settings));
            _activePaths.Add(path);
            _jobs[id] = new(id, name, "Queued", null, DateTimeOffset.UtcNow);
            discord.Enqueue(_jobs[id], path, settings);
            _ready.Writer.TryWrite(true);
            return true;
        }
    }

    public bool TryCancel(Guid id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job))
                return false;

            if (job.State == "Canceling")
                return true;

            if (job.State == "Running" && job.Phase != "Finishing")
            {
                _jobs[id] = job with { State = "Canceling", Phase = null, Percent = null };
                _runningCancellation!.Cancel();
                return true;
            }

            if (job.State != "Queued")
                return false;

            var pending = _pending.First;
            while (pending is not null && pending.Value.Id != id)
                pending = pending.Next;
            if (pending is null)
                return false;

            _pending.Remove(pending);
            FinishJob(id, pending.Value.Path, "Canceled", null);
            return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var _ in _ready.Reader.ReadAllAsync(stoppingToken))
        {
            while (!stoppingToken.IsCancellationRequested && await RunNextAsync(stoppingToken))
            {
            }
        }
    }

    private async Task<bool> RunNextAsync(CancellationToken stoppingToken)
    {
        (Guid id, string path, AudioSettings settings) work;
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_pending.First is not { } next)
                return false;

            work = next.Value;
            _pending.RemoveFirst();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _runningCancellation = cancellation;
            _jobs[work.id] = _jobs[work.id] with { State = "Running", Phase = "Preparing movie", Updated = DateTimeOffset.UtcNow };
        }

        var (id, path, settings) = work;
        var state = "Completed";
        string? error = null;
        using (cancellation)
        {
            try
            {
                await AudioProcessor.AdjustAsync(encoder.EncoderPath, encoder.ProbePath, path, settings, cancellation.Token, (phase, percent) =>
                {
                    lock (_gate)
                    {
                        if (_jobs[id].State == "Running")
                            _jobs[id] = _jobs[id] with { Phase = phase, Percent = percent };
                    }
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
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                state = stoppingToken.IsCancellationRequested ? "Stopped" : "Canceled";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audio adjustment failed for movie {ItemId}", id);
                state = "Failed";
                error = ex.Message;
            }
            finally
            {
                lock (_gate)
                {
                    _runningCancellation = null;
                    var finished = FinishJob(id, path, state, error);
                    if (state is "Completed" or "Failed")
                        discord.Enqueue(finished, path, settings);
                }
            }
        }
        return true;
    }

    private static bool IsActive(Job job) => job.State is "Queued" or "Running" or "Canceling";

    private Job FinishJob(Guid id, string path, string state, string? error)
    {
        _activePaths.Remove(path);
        var finished = _jobs[id] with
        {
            State = state,
            Message = error,
            Phase = null,
            Percent = null,
            Updated = DateTimeOffset.UtcNow
        };
        _jobs[id] = finished;

        foreach (var old in _jobs.Values.Where(job => !IsActive(job))
            .OrderByDescending(job => job.Updated).Skip(25).ToArray())
            _jobs.Remove(old.Id);

        return finished;
    }
}
