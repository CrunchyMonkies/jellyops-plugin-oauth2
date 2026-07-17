using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.OAuth2;

/// <summary>
/// Startup task that registers an <c>index.html</c> transformation with the File Transformation
/// plugin (if installed) to inject the SSO login-button client script into the Jellyfin web client.
/// Integration is purely reflective, so the plugin takes no hard dependency on File Transformation
/// and degrades gracefully (logs and continues) when it is absent.
/// </summary>
public sealed class FileTransformationIntegration : IScheduledTask
{
    private const string ScriptMarker = "/sso/ClientScript";

    private readonly ILogger<FileTransformationIntegration> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileTransformationIntegration"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public FileTransformationIntegration(ILogger<FileTransformationIntegration> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Keycloak SSO login-button registration";

    /// <inheritdoc />
    public string Key => "KeycloakSsoFileTransformation";

    /// <inheritdoc />
    public string Description => "Registers the SSO login-button script injection with the File Transformation plugin.";

    /// <inheritdoc />
    public string Category => "Keycloak SSO";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => new[] { new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger } };

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);

        try
        {
            var ftAssembly = AssemblyLoadContext.All
                .SelectMany(ctx => ctx.Assemblies)
                .FirstOrDefault(asm => asm.FullName?.Contains("Jellyfin.Plugin.FileTransformation", StringComparison.Ordinal) ?? false);

            if (ftAssembly == null)
            {
                _logger.LogInformation("[Keycloak SSO] File Transformation plugin not found. "
                    + "The SSO login button will not be injected — install the File Transformation plugin to enable it.");
                progress.Report(100);
                return Task.CompletedTask;
            }

            var pluginInterface = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            if (pluginInterface == null)
            {
                _logger.LogWarning("[Keycloak SSO] File Transformation plugin found but PluginInterface type not available. "
                    + "The installed version may be incompatible.");
                progress.Report(100);
                return Task.CompletedTask;
            }

            var registerMethod = pluginInterface.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
            if (registerMethod == null)
            {
                _logger.LogWarning("[Keycloak SSO] File Transformation plugin found but RegisterTransformation method not available. "
                    + "The installed version may be incompatible.");
                progress.Report(100);
                return Task.CompletedTask;
            }

            var payload = new JObject
            {
                ["id"] = (Plugin.Instance?.Id ?? Guid.Empty).ToString(),
                ["fileNamePattern"] = "index.html",
                ["callbackAssembly"] = typeof(FileTransformationIntegration).Assembly.FullName,
                ["callbackClass"] = typeof(FileTransformationIntegration).FullName,
                ["callbackMethod"] = nameof(TransformIndexHtml)
            };

            registerMethod.Invoke(null, new object?[] { payload });

            _logger.LogInformation("[Keycloak SSO] Registered index.html transformation with File Transformation plugin.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Keycloak SSO] Failed to register with File Transformation plugin. "
                + "The SSO login button will not be injected.");
        }

        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Callback invoked by the File Transformation plugin to inject the SSO login-button script tag
    /// into <c>index.html</c>. Idempotent: skips insertion when the marker is already present.
    /// </summary>
    /// <param name="payload">A <see cref="JObject"/> (or an object exposing a <c>contents</c> property) with the file text.</param>
    /// <returns>The transformed file contents.</returns>
    public static string TransformIndexHtml(object payload)
    {
        var contents = payload is JObject jobj
            ? jobj["contents"]?.ToString()
            : payload?.GetType().GetProperty("contents")?.GetValue(payload)?.ToString();

        if (string.IsNullOrEmpty(contents) || contents.Contains(ScriptMarker, StringComparison.Ordinal))
        {
            return contents ?? string.Empty;
        }

        var bodyEndIndex = contents.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyEndIndex >= 0)
        {
            return contents.Insert(bodyEndIndex, "    <script src=\"/sso/ClientScript\" defer></script>\n");
        }

        return contents;
    }
}
