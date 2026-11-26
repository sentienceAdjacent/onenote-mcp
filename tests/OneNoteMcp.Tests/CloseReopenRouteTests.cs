using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Round-3 retrospective F5 (story 4.15): the round trip from <c>close_notebook</c>'s reopen route to <c>open_notebook</c>.
/// Each notebook path runs through both tools with the same faked links, drive types and OneDrive folders: a path
/// <c>open_notebook</c> refuses gets "reopen in OneNote" from the close, and one it takes gets the <c>open_notebook</c> route.
/// Nothing here reaches real OneNote.
/// </summary>
public sealed class CloseReopenRouteTests
{
    private const string NotebookId = "{4B0B0000-0000-4000-8000-0000000415A1}{1}{B0}";
    private const string OneDriveFolder = @"C:\Users\Someone\OneDrive";
    private const string Ns = OneNoteXml.Namespace2013;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every path form <c>open_notebook</c> refuses before COM, by the path rule, with the refusal's userAction.</summary>
    public static readonly TheoryData<string, string> RefusedForms = new()
    {
        { @"\\server\share\Notebook E", OpenNotebookUseCase.PathUserAction },
        { @"\\localhost\c$\Notebooks\Notebook E", OpenNotebookUseCase.PathUserAction },
        { @"\\?\C:\Notebooks\Notebook E", OpenNotebookUseCase.PathUserAction },
        { @"Z:\Notebooks\Notebook E", OpenNotebookUseCase.PathUserAction },
        { @"C:\Links\ToServer", OpenNotebookUseCase.PathUserAction },
        { @"Notebooks\Notebook E", OpenNotebookUseCase.PathUserAction },
        { OneDriveFolder + @"\Notebooks\Notebook E", OpenNotebookUseCase.OneDriveUserAction },
    };

    [Theory]
    [MemberData(nameof(RefusedForms))]
    public async Task APathOpenNotebookRefuses_GetsTheReopenInOneNoteRoute(string path, string openUserAction)
    {
        var closeRoot = Ok(await CloseTool(path).CloseNotebook(NotebookId, Ct));

        var warning = Assert.Single(closeRoot.GetProperty("warnings").EnumerateArray());
        Assert.Equal(CloseNotebookUseCase.ReopenInOneNoteUserAction, warning.GetProperty("userAction").GetString());

        var openGateway = OpenGateway();
        var error = Error(await OpenTool(openGateway).OpenNotebook(path, Ct), "InvalidArgument");
        Assert.Equal(openUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(openGateway.OpenHierarchyCalls);
    }

    [Fact]
    public async Task ALocalPathOpenNotebookTakes_GetsTheOpenNotebookRoute()
    {
        const string path = @"C:\Notebooks\Notebook E";

        var closeRoot = Ok(await CloseTool(path).CloseNotebook(NotebookId, Ct));

        var warning = Assert.Single(closeRoot.GetProperty("warnings").EnumerateArray());
        Assert.Equal(CloseNotebookUseCase.ReopenLocalUserAction, warning.GetProperty("userAction").GetString());

        var openGateway = OpenGateway();
        Ok(await OpenTool(openGateway).OpenNotebook(path, Ct));
        Assert.Equal([path], openGateway.OpenHierarchyCalls);
    }

    [Fact]
    public async Task ACloudPath_KeepsTheCloudRoute()
    {
        var closeRoot = Ok(await CloseTool("https://d.docs.live.net/0000000000000000/Documents/Notebook E").CloseNotebook(NotebookId, Ct));

        var warning = Assert.Single(closeRoot.GetProperty("warnings").EnumerateArray());
        Assert.Equal(CloseNotebookUseCase.ReopenCloudUserAction, warning.GetProperty("userAction").GetString());
    }

    [Fact]
    public void AMappedNetworkDrive_IsRefusedAsNetwork_ByTheGate()
    {
        Assert.Null(FilePathGate.Canonical(@"Z:\Notebooks\Notebook E", Links(), out var refusal));
        Assert.Equal(FilePathRefusal.Network, refusal);
        Assert.NotNull(FilePathGate.Canonical(@"C:\Notebooks\Notebook E", Links(), out _));
    }

    private static FakeLinks Links() => new(new() { [@"C:\Links\ToServer"] = @"\\server\share\Notebook" }, networkDrives: [@"Z:\"]);

    private static OneDriveFolders OneDrive() => new([new OneDriveAccount(OneDriveFolder)]);

    private static CloseNotebookTool CloseTool(string notebookPath)
    {
        var gateway = new FakeGateway();
        gateway.Hierarchies[(string.Empty, HierarchyScope.Notebooks)] =
            "<one:Notebooks xmlns:one=\"" + Ns + "\"><one:Notebook name=\"Notebook E\" nickname=\"Notebook E\" ID=\"" + NotebookId
            + "\" path=\"" + System.Security.SecurityElement.Escape(notebookPath) + "\" lastModifiedTime=\"2026-11-26T10:00:00.000Z\" /></one:Notebooks>";
        var pipeline = new AppStatePipeline(gateway, writeLock: AppStatePipelineTests.TestLock());
        return new CloseNotebookTool(
            new ToolRunner(NullLogger<ToolRunner>.Instance),
            new CloseNotebookUseCase(gateway, pipeline, (_, _) => Task.CompletedTask, OneDrive, Links()));
    }

    private static FakeGateway OpenGateway()
    {
        var gateway = new FakeGateway();
        gateway.Hierarchies[(string.Empty, HierarchyScope.Notebooks)] = "<one:Notebooks xmlns:one=\"" + Ns + "\" />";
        return gateway;
    }

    private static OpenNotebookTool OpenTool(FakeGateway gateway) =>
        new(
            new ToolRunner(NullLogger<ToolRunner>.Instance),
            new OpenNotebookUseCase(gateway, new AppStatePipeline(gateway, writeLock: AppStatePipelineTests.TestLock()), OneDrive, null, Links(), new NotebookFolders()));

    private static JsonElement Ok(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        var root = result.StructuredContent.Value;
        Assert.False(result.IsError, root.GetRawText());
        return root;
    }

    private static JsonElement Error(CallToolResult result, string code)
    {
        Assert.True(result.IsError, result.StructuredContent?.GetRawText());
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        return error;
    }

    /// <summary>Scripted links and mapped network drives.</summary>
    private sealed class FakeLinks(Dictionary<string, string> links, string[] networkDrives) : FilePathGate.ILinkReader
    {
        public string? ImmediateTarget(string path) => links.GetValueOrDefault(path);

        public bool IsNetworkDrive(string root) => networkDrives.Contains(root, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every path is a folder; only a folder named "Notebook E" holds a <c>.onetoc2</c>.</summary>
    private sealed class NotebookFolders : INotebookFolderProbe
    {
        public PathEntry Entry(string path) => PathEntry.Folder;

        public bool HoldsNotebookToc(string folder) => folder.EndsWith(@"\Notebook E", StringComparison.OrdinalIgnoreCase);
    }
}
