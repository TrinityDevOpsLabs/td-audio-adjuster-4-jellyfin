using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AudioAdjuster4Jellyfin;

public sealed class DiscordNotifications : BackgroundService
{
    private readonly Channel<(Uri Webhook, AudioQueue.Job Job, string Path)> _pending =
        Channel.CreateBounded<(Uri, AudioQueue.Job, string)>(new BoundedChannelOptions(100) { SingleReader = true });
    private readonly HttpClient _client;
    private readonly ILibraryManager _library;
    private readonly ILogger<DiscordNotifications> _logger;

    public DiscordNotifications(ILibraryManager library, ILogger<DiscordNotifications> logger)
        : this(library, logger, new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    internal DiscordNotifications(ILibraryManager library, ILogger<DiscordNotifications> logger, HttpMessageHandler handler)
    {
        _library = library;
        _logger = logger;
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    internal static bool TryGetWebhook(string? value, out Uri webhook)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
            uri.Host == "discord.com" && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            Regex.IsMatch(uri.AbsolutePath, @"\A/api/webhooks/[0-9]+/[A-Za-z0-9_-]+\z"))
        {
            webhook = uri;
            return true;
        }

        webhook = null!;
        return false;
    }

    internal void Enqueue(AudioQueue.Job job, string path, AudioSettings settings)
    {
        if (settings.DiscordEnabled && TryGetWebhook(settings.DiscordWebhookUrl, out var webhook) &&
            !_pending.Writer.TryWrite((webhook, job, path)))
            _logger.LogWarning("Discord notification queue is full for movie {ItemId}", job.Id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (webhook, job, path) in _pending.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var payload = CreatePayload(job, path);
                for (var attempt = 0; ; attempt++)
                {
                    using var response = await _client.PostAsJsonAsync(webhook, payload, stoppingToken);
                    if (response.IsSuccessStatusCode)
                        break;

                    if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == 3)
                    {
                        _logger.LogWarning("Discord returned HTTP {StatusCode} for movie {ItemId}", (int)response.StatusCode, job.Id);
                        break;
                    }

                    var retry = response.Headers.RetryAfter;
                    var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(1);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 0.1, 30)), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not send Discord notification for movie {ItemId} ({ErrorType})", job.Id, ex.GetType().Name);
            }
        }
    }

    private object CreatePayload(AudioQueue.Job job, string path)
    {
        var (title, description, color, fields) = job.State switch
        {
            "Queued" => ($"Movie Adjustment Accepted: {job.Name}",
                "The item was accepted and queued for audio adjustment.", 0x3498DB,
                new[] { Field("Item Type", "Movie"), Field("Request Status", "Accepted"), Field("Item ID", job.Id.ToString("N"), false) }),
            "Completed" => ("Audio Adjustment Complete", $"Adjusted audio for {job.Name}.", 0x2ECC71,
                new[] { Field("Result", "Adjusted"), Field("Status", "Complete"), Field("Library", GetLibrary(job.Id)),
                    Field("Destination", path, false) }),
            _ => ("Audio Adjustment Error", $"ERROR item {job.Name}: {job.Message}", 0xE74C3C,
                new[] { Field("Status", "Failed"), Field("Action", "Check Jellyfin server logs"), Field("Library", GetLibrary(job.Id)) })
        };

        return new
        {
            embeds = new[]
            {
                new
                {
                    title = Limit(title, 256),
                    description = Limit(description, job.State == "Completed" ? 3500 : 4096),
                    color,
                    fields,
                    footer = new { text = "AudioAdjuster4Jellyfin" },
                    timestamp = job.Updated.UtcDateTime
                }
            },
            allowed_mentions = new { parse = Array.Empty<string>() }
        };
    }

    private string GetLibrary(Guid id)
    {
        try
        {
            if (_library.GetItemById(id) is { } item)
            {
                var names = string.Join(", ", _library.GetCollectionFolders(item).Select(folder => folder.Name).Distinct());
                if (!string.IsNullOrWhiteSpace(names))
                    return names;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not find the Jellyfin library for movie {ItemId}", id);
        }

        return "Unknown";
    }

    private static object Field(string name, string value, bool inline = true) =>
        new { name, value = Limit(value, 1024), inline };

    private static string Limit(string value, int length) => value.Length <= length
        ? value
        : value[..(char.IsHighSurrogate(value[length - 1]) ? length - 1 : length)];

    public override void Dispose()
    {
        base.Dispose();
        _client.Dispose();
    }
}
