using Jellyfin.Plugin.OAuth2.Auth;
using Jellyfin.Plugin.OAuth2.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.OAuth2;

/// <summary>
/// Registers the plugin's services with the Jellyfin host DI container. Runs after core service
/// registration, so adding an <see cref="IAuthenticationProvider"/> here augments the host's set.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(nameof(OidcService));
        serviceCollection.AddSingleton<OidcService>();
        serviceCollection.AddSingleton<OidcStateStore>();
        serviceCollection.AddSingleton<IAuthenticationProvider, OidcAuthenticationProvider>();
    }
}
