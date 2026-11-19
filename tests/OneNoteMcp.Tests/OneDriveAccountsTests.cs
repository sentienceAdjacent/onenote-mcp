using OneNoteMcp.Core.Access;
using OneNoteMcp.Server;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Backlog 13 (Phill, 2026-11-18, D1 (c)): the server's reader of the user's OneDrive folders, from every
/// <c>HKCU\Software\Microsoft\OneDrive\Accounts\*</c> key with a <c>UserFolder</c> and from <c>%OneDrive%</c>,
/// <c>%OneDriveConsumer%</c> and <c>%OneDriveCommercial%</c>, combined. Only the personal account gets an address, from
/// its <c>cid</c>. Every value here is a placeholder.
/// </summary>
public sealed class OneDriveAccountsTests
{
    private const string Cid = "0123456789abcdef";

    private sealed class FakeSource : IOneDriveSource
    {
        public List<OneDriveRegistryAccount> Accounts { get; } = [];

        public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

        public bool RegistryFails { get; set; }

        public int Reads { get; private set; }

        public IReadOnlyList<OneDriveRegistryAccount> RegistryAccounts()
        {
            Reads++;
            return RegistryFails ? throw new UnauthorizedAccessException("denied") : Accounts;
        }

        public string? Variable(string name) => Variables.TryGetValue(name, out var value) ? value : null;
    }

    [Fact]
    public void ThePersonalAccount_GetsItsAddressFromItsCid()
    {
        var source = new FakeSource();
        source.Accounts.Add(new OneDriveRegistryAccount("Personal", @"C:\Users\x\OneDrive", Cid.ToUpperInvariant()));

        var account = Assert.Single(OneDriveAccounts.Read(source).Accounts);

        Assert.Equal(@"C:\Users\x\OneDrive", account.Folder);
        Assert.Equal($"https://d.docs.live.net/{Cid}/", account.UrlPrefix);
    }

    [Fact]
    public void ABusinessAccount_GetsNoAddress()
    {
        var source = new FakeSource();
        source.Accounts.Add(new OneDriveRegistryAccount("Business1", @"C:\Users\x\OneDrive - Contoso", Cid));

        Assert.Null(Assert.Single(OneDriveAccounts.Read(source).Accounts).UrlPrefix);
    }

    [Theory]
    [InlineData("OneDrive")]
    [InlineData("OneDriveConsumer")]
    [InlineData("OneDriveCommercial")]
    public void AFolderKnownOnlyFromTheEnvironment_Counts(string variable)
    {
        var source = new FakeSource();
        source.Variables[variable] = @"C:\Users\x\OneDrive";

        Assert.Equal(@"C:\Users\x\OneDrive", Assert.Single(OneDriveAccounts.Read(source).Accounts).Folder);
    }

    [Fact]
    public void TheSameFolderFromBothSources_IsOneAccount_WithItsAddress()
    {
        var source = new FakeSource();
        source.Variables["OneDrive"] = @"C:\Users\x\OneDrive\";
        source.Variables["OneDriveConsumer"] = @"c:\users\x\onedrive";
        source.Accounts.Add(new OneDriveRegistryAccount("Personal", @"C:\Users\x\OneDrive", Cid));

        Assert.Equal($"https://d.docs.live.net/{Cid}/", Assert.Single(OneDriveAccounts.Read(source).Accounts).UrlPrefix);
    }

    [Fact]
    public void MissingOrUnusableValues_AreSkipped_AndABadCidGivesNoAddress()
    {
        var source = new FakeSource();
        source.Accounts.Add(new OneDriveRegistryAccount("Personal", null, Cid));
        source.Accounts.Add(new OneDriveRegistryAccount("Business1", "relative\\folder", null));
        source.Accounts.Add(new OneDriveRegistryAccount("Personal", @"D:\OneDrive", "not/a cid"));
        source.Variables["OneDrive"] = "   ";
        source.Variables["OneDriveCommercial"] = "https://example.org/x";

        var account = Assert.Single(OneDriveAccounts.Read(source).Accounts);
        Assert.Equal(@"D:\OneDrive", account.Folder);
        Assert.Null(account.UrlPrefix);
    }

    [Fact]
    public void AnUnreadableRegistry_StillLeavesTheEnvironment()
    {
        var source = new FakeSource { RegistryFails = true };
        source.Variables["OneDrive"] = @"C:\Users\x\OneDrive";

        Assert.Single(OneDriveAccounts.Read(source).Accounts);
    }

    [Fact]
    public void TheFoldersAreKeptFor30Seconds_ThenReadAgain()
    {
        var source = new FakeSource();
        var clock = new FakeClock();
        var accounts = new OneDriveAccounts(source, clock);

        Assert.Empty(accounts.Current().Accounts);
        source.Accounts.Add(new OneDriveRegistryAccount("Personal", @"C:\Users\x\OneDrive", Cid));
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(accounts.Current().Accounts);
        Assert.Equal(1, source.Reads);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(accounts.Current().Accounts);
        Assert.Equal(2, source.Reads);
    }
}
