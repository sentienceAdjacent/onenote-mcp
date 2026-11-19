using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Server;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// The 3.8 S2 report, end to end through the real server registration (<see cref="ServerSetup.AddOneNoteMcp"/>) over an
/// MCP client, with <see cref="FakeGateway"/>: <c>publish</c>-made package opened with <c>open_package</c> under the
/// export root, then <c>get_hierarchy</c> of one of the copy's sections at scope <c>pages</c>. The client declares no
/// elicitation, so a protected section must be refused with <c>ReadNotGranted</c> and its page never listed.
/// </summary>
public sealed class OpenedCopyServerTests : IAsyncLifetime
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string SectionId = "{SEC00000-0000-4000-8000-000000000001}{1}{B0}";

    private readonly TempDirectory dir = new();
    private readonly FakeGateway gateway = new();
    private readonly Pipe clientToServer = new();
    private readonly Pipe serverToClient = new();
    private IHost? host;
    private McpClient? client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Root => dir.Combine("exports");

    public async ValueTask InitializeAsync()
    {
        File.WriteAllText(dir.Combine("settings.json"), JsonSerializer.Serialize(new { schemaVersion = 1, exportRoots = new[] { Root } }));
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.ClearProviders();
        builder.Services
            .AddOneNoteMcp()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        builder.Services.AddSingleton<IOneNoteGateway>(gateway);
        builder.Services.AddSingleton(new SettingsStore(dir.Combine("settings.json"), dir.Combine("journal")));
        host = builder.Build();
        await host.StartAsync(Ct);
        client = await McpClient.CreateAsync(new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()), cancellationToken: Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (client is not null)
        {
            await client.DisposeAsync();
        }

        if (host is not null)
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }

        dir.Dispose();
    }

    [Fact]
    public async Task AfterOpenPackage_ASectionOfTheCopy_AtPagesScope_IsReadNotGranted_ItsPageNeverListed()
    {
        Directory.CreateDirectory(dir.Combine("downloads"));
        var package = dir.Combine(@"downloads\Throwaway a.onepkg");
        File.WriteAllBytes(package, [1, 2, 3, 4]);

        var opened = await client!.CallToolAsync("open_package", new Dictionary<string, object?> { ["package"] = package }, cancellationToken: Ct);
        Assert.NotEqual(true, opened.IsError);
        var folder = opened.StructuredContent!.Value.GetProperty("meta").GetProperty("outputPath").GetString()!;
        Section(Path.Combine(folder, "New Section 1.one"));

        var listed = await client.CallToolAsync("get_hierarchy", new Dictionary<string, object?> { ["startNodeId"] = SectionId, ["scope"] = "pages" }, cancellationToken: Ct);

        var text = listed.StructuredContent!.Value.GetRawText();
        Assert.True(listed.IsError, text);
        Assert.Equal("ReadNotGranted", listed.StructuredContent.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("Secret page", text, StringComparison.Ordinal);
    }

    private void Section(string path)
    {
        gateway.Parents[SectionId] = gateway.OpenedId;
        gateway.Parents[gateway.OpenedId] = string.Empty;
        var attributes = $"name=\"New Section 1\" ID=\"{SectionId}\" path=\"{path}\" lastModifiedTime=\"2026-11-18T00:00:00.000Z\"";
        gateway.Hierarchies[(SectionId, HierarchyScope.Pages)] = $"<?xml version=\"1.0\"?>\n<one:Section {Ns} {attributes}><one:Page ID=\"{{P}}\" name=\"Secret page\" /></one:Section>";
        gateway.Hierarchies[(SectionId, HierarchyScope.Self)] = $"<?xml version=\"1.0\"?>\n<one:Section {Ns} {attributes} />";
        gateway.Hierarchies[(gateway.OpenedId, HierarchyScope.Self)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebook {Ns} name=\"Throwaway a\" ID=\"{gateway.OpenedId}\" path=\"{Path.GetDirectoryName(path)}\" />";
    }
}
