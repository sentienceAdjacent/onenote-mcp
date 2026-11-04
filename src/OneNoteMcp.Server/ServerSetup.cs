using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server.Tools;

namespace OneNoteMcp.Server;

/// <summary>
/// The one service registration, shared by <c>Program</c> and the tool-contract test so both see the same server.
/// The transport is added by the caller.
/// </summary>
public static class ServerSetup
{
    public const string ServerName = "OneNoteMcp";

    /// <summary>
    /// <c>&lt;Version&gt;</c> from <c>Directory.Build.props</c>: the release tag (AD-19) and <c>serverInfo.version</c>.
    /// Its major.minor is the tool contract version (AD-14).
    /// </summary>
    public static string Version { get; } = ReadVersion();

    public static IMcpServerBuilder AddOneNoteMcp(this IServiceCollection services)
    {
        services.AddSingleton<IProcessProbe, ProcessProbe>();
        services.AddSingleton<IOneNoteGateway, ComGateway>();
        services.AddSingleton<ToolRunner>();
        services.AddSingleton<GetHierarchyUseCase>();

        return services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = ServerName, Version = Version })
            .WithTools<GetHierarchyTool>(EnvelopeJson.Options);
    }

    private static string ReadVersion()
    {
        var assembly = typeof(ServerSetup).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            // Drop the "+<commit>" source-revision suffix the SDK appends.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
