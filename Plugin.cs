using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace AudioAdjuster4Jellyfin;

public sealed class Plugin : BasePlugin<AudioSettings>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer, IServerApplicationHost applicationHost)
        : base(paths, serializer)
    {
        if (!SupportsServer(applicationHost))
            throw new NotSupportedException("AudioAdjuster4Jellyfin requires Jellyfin Server 12.x.");

        Instance = this;
    }

    internal static bool SupportsServer(IServerApplicationHost applicationHost) => applicationHost.ApplicationVersion.Major == 12;

    internal static Plugin Instance { get; private set; } = null!;
    public static readonly Guid PluginId = new("175273b8-46b1-4059-97f7-31931b194342");
    public override Guid Id => PluginId;
    public override string Name => "AudioAdjuster4Jellyfin";

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is AudioSettings { DiscordEnabled: true } settings
            && !DiscordNotifications.TryGetWebhook(settings.DiscordWebhookUrl, out _))
            throw new ArgumentException("Enter a valid Discord webhook URL to enable notifications.", nameof(configuration));

        base.UpdateConfiguration(configuration);
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            DisplayName = Name,
            EmbeddedResourcePath = "AudioAdjuster4Jellyfin.Configuration.config.html",
            EnableInMainMenu = true,
            MenuSection = "plugins",
            MenuIcon = "volume_up"
        };
    }
}

public sealed class AudioSettings : BasePluginConfiguration
{
    public string Mode { get; set; } = "Percentage";
    public int VolumePercent { get; set; } = 80;
    public double GainDb { get; set; } = -2;
    public double TargetLufs { get; set; } = -23;
    public string OutputMode { get; set; } = "Replace";
    public bool KeepBackup { get; set; } = true;
    public string LoggingLevel { get; set; } = "Info";
    public bool DiscordEnabled { get; set; }
    public string DiscordWebhookUrl { get; set; } = string.Empty;
}

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        if (!Plugin.SupportsServer(applicationHost))
            return;

        services.AddSingleton<DiscordNotifications>();
        services.AddHostedService(provider => provider.GetRequiredService<DiscordNotifications>());
        services.AddSingleton<AudioQueue>();
        services.AddHostedService(provider => provider.GetRequiredService<AudioQueue>());
    }
}
