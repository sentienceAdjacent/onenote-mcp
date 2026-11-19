using Microsoft.Win32;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Server;

/// <summary>One <c>HKCU\Software\Microsoft\OneDrive\Accounts\&lt;key&gt;</c> entry: its key name and the two values read.</summary>
public sealed record OneDriveRegistryAccount(string KeyName, string? UserFolder, string? Cid);

/// <summary>Where <see cref="OneDriveAccounts"/> reads from: the registry and the environment. Tests fake it.</summary>
public interface IOneDriveSource
{
    /// <summary>Each account key under <c>HKCU\Software\Microsoft\OneDrive\Accounts</c>. May throw; the reader then uses the environment only.</summary>
    IReadOnlyList<OneDriveRegistryAccount> RegistryAccounts();

    /// <summary>An environment variable of this process, or null.</summary>
    string? Variable(string name);
}

/// <summary>
/// Backlog 13 (Phill, 2026-11-18, D1 (c)): the OneDrive folders on this machine, for <see cref="Protection"/>. Every
/// account key with a <c>UserFolder</c>, and <c>%OneDrive%</c>, <c>%OneDriveConsumer%</c> and <c>%OneDriveCommercial%</c>,
/// combined; the <c>Personal</c> key's <c>cid</c> (hex) gives its address <c>https://d.docs.live.net/&lt;cid&gt;/</c>; a
/// business account gets none (its value names are unverified; the tail covers it). Reads values only, writes nothing, and
/// keeps what it read for <see cref="CacheFor"/>, so an account signed in later is known within that time.
/// </summary>
public sealed class OneDriveAccounts
{
    /// <summary>How long a read is kept.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private static readonly string[] Variables = ["OneDrive", "OneDriveConsumer", "OneDriveCommercial"];

    private readonly IOneDriveSource source;
    private readonly TimeProvider clock;
    private readonly Lock gate = new();
    private (DateTimeOffset At, OneDriveFolders Folders)? last;

    public OneDriveAccounts(IOneDriveSource source, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clock);
        this.source = source;
        this.clock = clock;
    }

    /// <summary>The reader for this user: the real registry and this process's environment, on the system clock.</summary>
    public static OneDriveAccounts ForThisUser() => new(new UserSource(), TimeProvider.System);

    /// <summary>The folders now, read at most once per <see cref="CacheFor"/>.</summary>
    public OneDriveFolders Current()
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (last is { } cached && now - cached.At < CacheFor)
            {
                return cached.Folders;
            }

            var folders = Read(source);
            last = (now, folders);
            return folders;
        }
    }

    /// <summary>One read of <paramref name="source"/>: the registry's accounts first (so an address is kept), then the environment.</summary>
    public static OneDriveFolders Read(IOneDriveSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var accounts = new List<OneDriveAccount>();
        IReadOnlyList<OneDriveRegistryAccount> registry;
        try
        {
            registry = source.RegistryAccounts();
        }
#pragma warning disable CA1031 // Any registry failure leaves the environment as the only source; the rule still fails closed.
        catch (Exception)
#pragma warning restore CA1031
        {
            registry = [];
        }

        foreach (var key in registry)
        {
            if (Usable(key.UserFolder) is { } folder)
            {
                var personal = string.Equals(key.KeyName, "Personal", StringComparison.OrdinalIgnoreCase);
                accounts.Add(new OneDriveAccount(folder, personal && IsHex(key.Cid) ? $"https://d.docs.live.net/{key.Cid!.ToLowerInvariant()}/" : null));
            }
        }

        foreach (var name in Variables)
        {
            if (Usable(source.Variable(name)) is { } folder)
            {
                accounts.Add(new OneDriveAccount(folder));
            }
        }

        return new OneDriveFolders(accounts);
    }

    private static string? Usable(string? folder) => PathUnder.Full(folder?.Trim()) is not null ? folder!.Trim() : null;

    private static bool IsHex(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(c => char.IsAsciiHexDigit(c));

    /// <summary>The real sources: <c>HKCU</c> and this process's environment.</summary>
    private sealed class UserSource : IOneDriveSource
    {
        private const string AccountsKey = @"Software\Microsoft\OneDrive\Accounts";

        public IReadOnlyList<OneDriveRegistryAccount> RegistryAccounts()
        {
            using var accounts = Registry.CurrentUser.OpenSubKey(AccountsKey);
            if (accounts is null)
            {
                return [];
            }

            var found = new List<OneDriveRegistryAccount>();
            foreach (var name in accounts.GetSubKeyNames())
            {
                using var key = accounts.OpenSubKey(name);
                found.Add(new OneDriveRegistryAccount(name, key?.GetValue("UserFolder") as string, key?.GetValue("cid") as string));
            }

            return found;
        }

        public string? Variable(string name) => Environment.GetEnvironmentVariable(name);
    }
}
