using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AudioAdjuster4Jellyfin.Tests;

public sealed class PluginTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("audio-plugin-test-").FullName;

    private readonly IMediaSourceManager _originalMediaSourceManager = BaseItem.MediaSourceManager;

    [Fact]
    public async Task ControllerValidatesAndQueuesSupportedMovies()
    {
        var mediaSources = new Mock<IMediaSourceManager>();
        mediaSources.Setup(x => x.GetPathProtocol(It.IsAny<string>())).Returns(MediaProtocol.File);
        BaseItem.MediaSourceManager = mediaSources.Object;
        var plugin = CreatePlugin(12);
        var settings = new AudioSettings();
        Assert.Equal("Info", settings.LoggingLevel);
        plugin.UpdateConfiguration(settings);
        Assert.Same(settings, plugin.Configuration);
        Assert.Throws<ArgumentException>(() => plugin.UpdateConfiguration(new AudioSettings { DiscordEnabled = true }));

        var path = await AudioProcessorTests.CreateMovieAsync(_directory);
        var id = Guid.NewGuid();
        var movie = new Movie { Id = id, Name = "Sample", Path = path };
        var invalid = new Movie { Id = Guid.NewGuid(), Name = "Missing", Path = Path.Combine(_directory, "missing.mkv") };
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetItemById(id)).Returns(movie);
        library.Setup(x => x.GetItemById(invalid.Id)).Returns(invalid);
        library.Setup(x => x.GetCollectionFolders(movie)).Returns(new List<Folder>
        {
            new() { Name = "Movies" }, new() { Name = "Favorites" }, new() { Name = "Movies" }
        });
        using var discord = new DiscordNotifications(library.Object, NullLogger<DiscordNotifications>.Instance);
        using var queue = new AudioQueue(Mock.Of<IMediaEncoder>(), Mock.Of<ILibraryMonitor>(), NullLogger<AudioQueue>.Instance, discord);
        var controller = new AudioController(library.Object, queue);
        var script = Assert.IsType<FileStreamResult>(controller.Script());
        await script.FileStream.DisposeAsync();
        Assert.Equal("application/javascript", script.ContentType);
        Assert.IsType<NotFoundObjectResult>(controller.Submit(Guid.NewGuid()));
        Assert.IsType<BadRequestObjectResult>(controller.Submit(invalid.Id));
        Assert.IsType<AcceptedResult>(controller.Submit(id));
        Assert.IsType<ConflictObjectResult>(controller.Submit(id));
        Assert.IsType<OkObjectResult>(controller.MovieStatus(id));
        Assert.Single(Assert.IsType<AudioQueue.Job[]>(Assert.IsType<OkObjectResult>(controller.Jobs()).Value));
        Assert.IsType<BadRequestResult>(controller.MovieLibraries(new Guid[11]));
        var folders = Assert.IsType<Dictionary<string, string[]>>(
            Assert.IsType<OkObjectResult>(controller.MovieLibraries([id, Guid.NewGuid()])).Value);
        Assert.Equal(new[] { "Favorites", "Movies" }, folders[id.ToString("N")]);
        Assert.IsType<AcceptedResult>(controller.Cancel(id));
        Assert.IsType<ConflictObjectResult>(controller.Cancel(id));
    }

    [Fact]
    public void RegistersWorkersOnlyForSupportedJellyfin()
    {
        Assert.Throws<NotSupportedException>(() => CreatePlugin(11));
        var page = Assert.Single(CreatePlugin(12).GetPages());
        Assert.Equal("AudioAdjuster4Jellyfin", page.Name);
        Assert.True(page.EnableInMainMenu);

        var services = new ServiceCollection();
        var registrator = new ServiceRegistrator();
        registrator.RegisterServices(services, Host(11));
        Assert.Empty(services);
        services.AddLogging();
        services.AddSingleton(Mock.Of<IMediaEncoder>());
        services.AddSingleton(Mock.Of<ILibraryMonitor>());
        services.AddSingleton(Mock.Of<ILibraryManager>());
        registrator.RegisterServices(services, Host(12));
        using var provider = services.BuildServiceProvider();
        var workers = provider.GetServices<IHostedService>().ToArray();
        Assert.Contains(provider.GetRequiredService<AudioQueue>(), workers);
        Assert.Contains(provider.GetRequiredService<DiscordNotifications>(), workers);
    }

    private Plugin CreatePlugin(int major)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(x => x.PluginsPath).Returns(_directory);
        paths.SetupGet(x => x.PluginConfigurationsPath).Returns(_directory);
        return new Plugin(paths.Object, Mock.Of<IXmlSerializer>(), Host(major));
    }

    private static IServerApplicationHost Host(int major)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(x => x.ApplicationVersion).Returns(new Version(major, 0));
        return host.Object;
    }

    public void Dispose()
    {
        BaseItem.MediaSourceManager = _originalMediaSourceManager;
        Directory.Delete(_directory, recursive: true);
    }
}
