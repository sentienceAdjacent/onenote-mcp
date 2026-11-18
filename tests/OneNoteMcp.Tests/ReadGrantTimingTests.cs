using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;
using Xunit;
using V = OneNoteMcp.Tests.VaultGateway;

namespace OneNoteMcp.Tests;

/// <summary>
/// Backlog 9 (AD-7, AD-21): a write counts a read grant only when it was given at or before the call's start and is
/// unexpired then. A stored grant saved, or a <c>once</c> window opened, after the call started doesn't count; the
/// location checks a rename or move makes still count any unexpired grant.
/// </summary>
public sealed class ReadGrantTimingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string VaultKey = GrantKeys.SectionKey(V.VaultPath);

    // --- Stored grants --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stored_GivenBeforeTheCallStart_Counts()
    {
        using var h = new VaultHarness();
        StoreGrant(h, h.Clock.GetUtcNow());
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var callStart = h.Access.Now();

        Assert.True(h.Access.HoldsReadGrant(await Vault(h), callStart));
        Assert.True(h.Access.HoldsReadGrantGivenBy(VaultKey, callStart));
    }

    [Fact]
    public async Task Stored_GivenAtTheCallStart_Counts()
    {
        using var h = new VaultHarness();
        var callStart = h.Access.Now();
        StoreGrant(h, callStart);

        Assert.True(h.Access.HoldsReadGrant(await Vault(h), callStart));
    }

    [Fact]
    public async Task Stored_GivenDuringTheCall_DoesNotCount()
    {
        using var h = new VaultHarness();
        var callStart = h.Access.Now();
        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        StoreGrant(h, h.Clock.GetUtcNow());

        Assert.False(h.Access.HoldsReadGrant(await Vault(h), callStart));
        Assert.False(h.Access.HoldsReadGrantGivenBy(VaultKey, callStart));

        // A retry, starting after the grant was given, passes.
        Assert.True(h.Access.HoldsReadGrant(await Vault(h), h.Access.Now()));
    }

    [Fact]
    public async Task Stored_ReGivenDuringTheCall_DoesNotCount()
    {
        // Phill, 2026-11-17, Q1 (a): a grant held at the call's start and replaced during it counts as given after it.
        using var h = new VaultHarness();
        StoreGrant(h, h.Clock.GetUtcNow());
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        var callStart = h.Access.Now();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        StoreGrant(h, h.Clock.GetUtcNow());

        Assert.False(h.Access.HoldsReadGrant(await Vault(h), callStart));
        Assert.True(h.Access.HoldsReadGrant(await Vault(h), h.Access.Now()));
    }

    [Fact]
    public async Task Stored_ExpiredAtTheCallStart_DoesNotCount()
    {
        using var h = new VaultHarness();
        var given = h.Clock.GetUtcNow();
        StoreGrant(h, given, expiresAt: given + TimeSpan.FromHours(1));
        h.Clock.Advance(TimeSpan.FromHours(1));

        Assert.False(h.Access.HoldsReadGrant(await Vault(h), h.Access.Now()));
        Assert.False(h.Access.HoldsReadGrantGivenBy(VaultKey, h.Access.Now()));
    }

    [Fact]
    public async Task Stored_GrantedAtInTheFuture_LetsAReadPass_ButNoWriteUntilTheClockPassesIt()
    {
        // A clock stepped back, or a hand-edited file: fail closed for writes (plan matrix, future grantedAt).
        using var h = new VaultHarness();
        StoreGrant(h, h.Clock.GetUtcNow() + TimeSpan.FromHours(1));
        var vault = await Vault(h);
        var prompt = new FakePrompt();

        var read = await h.Access.CheckSectionAsync(new ToolCallContext("read", Prompt: prompt), vault, Ct);

        Assert.NotNull(read.Grant);
        Assert.Empty(prompt.Questions);
        Assert.False(h.Access.HoldsReadGrant(vault, h.Access.Now()));

        h.Clock.Advance(TimeSpan.FromHours(1));
        Assert.True(h.Access.HoldsReadGrant(vault, h.Access.Now()));
    }

    // --- Once windows ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Window_OpenedBeforeTheCallStart_Counts()
    {
        using var h = new VaultHarness();
        var vault = await Vault(h);
        await OpenWindow(h, vault);
        h.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(h.Access.HoldsReadGrant(vault, h.Access.Now()));
        Assert.True(h.Access.HoldsReadGrantGivenBy(VaultKey, h.Access.Now()));
    }

    [Fact]
    public async Task Window_OpenedAtTheCallStart_Counts()
    {
        using var h = new VaultHarness();
        var vault = await Vault(h);
        var callStart = h.Access.Now();
        await OpenWindow(h, vault);

        Assert.True(h.Access.HoldsReadGrant(vault, callStart));
    }

    [Fact]
    public async Task Window_OpenedDuringTheCall_DoesNotCount()
    {
        using var h = new VaultHarness();
        var vault = await Vault(h);
        var callStart = h.Access.Now();
        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await OpenWindow(h, vault);

        Assert.False(h.Access.HoldsReadGrant(vault, callStart));
        Assert.False(h.Access.HoldsReadGrantGivenBy(VaultKey, callStart));
        Assert.True(h.Access.HoldsReadGrant(vault, h.Access.Now()));
    }

    [Fact]
    public async Task Window_ReopenedDuringTheCall_DoesNotCount()
    {
        // A read reopens a window only once it has closed: here the window open at the call's start closes during the
        // call and a read opens a new one. The new window replaces the old, and was given after the start (Q1 a).
        using var h = new VaultHarness();
        var vault = await Vault(h);
        await OpenWindow(h, vault);
        h.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        var callStart = h.Access.Now();
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await OpenWindow(h, vault);

        Assert.False(h.Access.HoldsReadGrant(vault, callStart));
    }

    [Fact]
    public async Task Window_ExpiredAtTheCallStart_DoesNotCount()
    {
        using var h = new VaultHarness();
        var vault = await Vault(h);
        await OpenWindow(h, vault);
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        Assert.False(h.Access.HoldsReadGrant(vault, h.Access.Now()));
    }

    // --- Location checks keep counting any unexpired grant ---------------------------------------------------------

    [Fact]
    public void Location_AStoredGrantGivenAfterAt_StillCounts()
    {
        using var h = new VaultHarness();
        var at = h.Access.Now();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        StoreGrant(h, h.Clock.GetUtcNow());

        Assert.True(h.Access.HoldsReadGrantAtLocation(VaultKey, at));
    }

    [Fact]
    public async Task Location_AWindowOpenedAfterAt_StillCounts()
    {
        using var h = new VaultHarness();
        var vault = await Vault(h);
        var at = h.Access.Now();
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await OpenWindow(h, vault);

        Assert.True(h.Access.HoldsReadGrantAtLocation(VaultKey, at));
    }

    private static async Task<HierarchyNode> Vault(VaultHarness h) =>
        HierarchyNode.Root(await h.Gateway.GetHierarchyAsync(V.VaultId, HierarchyScope.Self, Ct));

    private static void StoreGrant(VaultHarness h, DateTimeOffset grantedAt, DateTimeOffset? expiresAt = null) =>
        h.Writer.SaveReadGrant(new ProtectedSectionGrant(
            VaultKey, V.VaultName, GrantKeys.NotebookKey(V.NotebookPath), V.VaultId, grantedAt, GrantedVia.Cli, expiresAt));

    private static async Task OpenWindow(VaultHarness h, HierarchyNode vault)
    {
        var prompt = new FakePrompt(FakePrompt.Accept(V.VaultName, ReadGrantLifetime.Once));
        var access = await h.Access.CheckSectionAsync(new ToolCallContext("read", Prompt: prompt), vault, Ct);
        Assert.NotNull(access.Grant);
        Assert.Single(prompt.Questions);
    }
}
