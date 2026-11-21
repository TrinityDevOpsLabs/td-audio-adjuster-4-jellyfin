using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AudioAdjuster4Jellyfin.Tests;

public sealed class DiscordNotificationsTests
{
    private const string Webhook = "https://discord.com/api/webhooks/123/test-token";

    [Theory]
    [InlineData(Webhook, true)]
    [InlineData("https://discord.com.example/api/webhooks/123/test-token", false)]
    [InlineData("http://discord.com/api/webhooks/123/test-token", false)]
    [InlineData("https://discord.com/api/webhooks/123/test-token?wait=true", false)]
    [InlineData(null, false)]
    public void ValidatesWebhook(string? url, bool expected)
    {
        Assert.Equal(expected, DiscordNotifications.TryGetWebhook(url, out var result));
        Assert.Equal(expected, result is not null);
    }

    [Theory]
    [InlineData("Queued", 0x3498DB, "Item ID")]
    [InlineData("Completed", 0x2ECC71, "Destination")]
    [InlineData("Failed", 0xE74C3C, "Action")]
    public async Task SendsJobEmbedAndRetriesRateLimit(string state, int color, string field)
    {
        var id = Guid.NewGuid();
        var movie = new Movie();
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetItemById(id)).Returns(movie);
        library.Setup(x => x.GetCollectionFolders(movie)).Returns(new List<Folder> { new() { Name = "Movies" } });
        var handler = new RecordingHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.NoContent);
        using var notifier = new DiscordNotifications(library.Object, NullLogger<DiscordNotifications>.Instance, handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await notifier.StartAsync(timeout.Token);
        try
        {
            var job = new AudioQueue.Job(id, new string('x', 300), state, "Example failure", DateTimeOffset.UtcNow);
            notifier.Enqueue(job, "/movies/sample.mkv", new AudioSettings());
            notifier.Enqueue(job, "/movies/sample.mkv", new AudioSettings { DiscordEnabled = true, DiscordWebhookUrl = Webhook });

            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var json = JsonDocument.Parse(await handler.Bodies.Reader.ReadAsync(timeout.Token));
                var embed = json.RootElement.GetProperty("embeds")[0];
                Assert.Equal(color, embed.GetProperty("color").GetInt32());
                Assert.InRange(embed.GetProperty("title").GetString()!.Length, 1, 256);
                var fields = embed.GetProperty("fields").EnumerateArray().ToDictionary(
                    element => element.GetProperty("name").GetString()!,
                    element => element.GetProperty("value").GetString());
                Assert.Contains(field, fields.Keys);
                if (state != "Queued")
                    Assert.Equal("Movies", fields["Library"]);
                Assert.Equal(0, json.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
            }
        }
        finally
        {
            await notifier.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ContinuesAfterTransportAndHttpFailures()
    {
        var handler = new RecordingHandler(null, HttpStatusCode.BadRequest, HttpStatusCode.NoContent);
        using var notifier = new DiscordNotifications(Mock.Of<ILibraryManager>(), NullLogger<DiscordNotifications>.Instance, handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await notifier.StartAsync(timeout.Token);
        try
        {
            var settings = new AudioSettings { DiscordEnabled = true, DiscordWebhookUrl = Webhook };
            for (var n = 0; n < 3; n++)
                notifier.Enqueue(new AudioQueue.Job(Guid.NewGuid(), $"Movie {n}", "Completed", null, DateTimeOffset.UtcNow), "/movie", settings);

            for (var n = 0; n < 3; n++)
            {
                var payload = await handler.Bodies.Reader.ReadAsync(timeout.Token);
                Assert.Contains($"Movie {n}", payload);
                Assert.Contains("Unknown", payload);
            }
        }
        finally
        {
            await notifier.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RecordingHandler(params HttpStatusCode?[] statuses) : HttpMessageHandler
    {
        internal Channel<string> Bodies { get; } = Channel.CreateUnbounded<string>();
        private int _index;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(Webhook, request.RequestUri?.ToString());
            var status = statuses[_index++];
            await Bodies.Writer.WriteAsync(await request.Content!.ReadAsStringAsync(cancellationToken), cancellationToken);
            if (status is null)
                throw new HttpRequestException("Simulated transport failure");

            var response = new HttpResponseMessage(status.Value);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        }
    }
}
