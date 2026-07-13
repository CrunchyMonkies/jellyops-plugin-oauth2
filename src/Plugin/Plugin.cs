using System;
using System.Collections.Generic;
using Jellyfin.Plugin.OAuth2.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.OAuth2;

/// <summary>
/// The Keycloak SSO plugin. Runs the OpenID Connect Authorization-Code flow against an external
/// Keycloak realm, auto-provisions Jellyfin users, and mints native Jellyfin sessions for auto-login.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Keycloak SSO";

    /// <inheritdoc />
    public override Guid Id => new("f1e2d3c4-b5a6-4978-8a9b-0c1d2e3f4a5b");

    /// <inheritdoc />
    public override string Description => "Keycloak/OIDC single sign-on with auto-login for Jellyfin.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "KeycloakSSO",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
        },
    ];
}
