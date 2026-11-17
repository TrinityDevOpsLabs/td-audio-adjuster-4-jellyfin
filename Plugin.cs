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

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            DisplayName = "Audio Adjuster",
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
}

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        if (!Plugin.SupportsServer(applicationHost))
            return;

        services.AddSingleton<AudioQueue>();
        services.AddHostedService(provider => provider.GetRequiredService<AudioQueue>());
    }
}
