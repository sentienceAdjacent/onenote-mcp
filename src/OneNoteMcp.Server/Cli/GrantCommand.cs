using System.Globalization;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Server.Cli;

/// <summary>
/// <c>OneNoteMcp grant</c> (AD-6, AD-21): the interactive command through which the user grants or revokes a read of a
/// password-protected section. It refuses to run without an interactive terminal, so a model can't drive it.
/// <list type="bullet">
/// <item><c>grant --read-section [name]</c>: the typed name is matched, trimmed and ignoring case, against the protected
/// sections of the open notebooks. Several matches are listed with their notebook and section groups, and the user picks
/// one. It offers <c>hours</c> (1 to 168, default 8) and <c>untilRevoked</c>; <c>once</c> exists only in the dialog.</item>
/// <item><c>grant --revoke --read-section [name]</c>: removes a stored read grant, matched the same way against the
/// stored grants (by the section's current name when it is open, else its stored name). It also runs on a
/// <c>settings.json</c> that is invalid only because two keys differ in case, so the user can remove one.</item>
/// </list>
/// The grant is keyed by the section's live <c>path</c> through <see cref="GrantKeys.SectionKey"/>, and written by
/// <see cref="SettingsWriter"/>, which never holds the settings lock while the user types.
/// </summary>
public sealed class GrantCommand
{
    public const string Usage =
        "Usage:\n"
        + "  OneNoteMcp grant --read-section [\"<section name>\"]            allow reading a password-protected section\n"
        + "  OneNoteMcp grant --revoke --read-section [\"<section name>\"]   remove that permission\n"
        + "Run it yourself in a terminal: it asks you to confirm, and it refuses to run without an interactive terminal.";

    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly bool interactive;
    private readonly Func<CancellationToken, Task<string>> readSections;
    private readonly SettingsWriter writer;
    private readonly TimeProvider clock;

    /// <param name="readSections">Returns <c>GetHierarchy</c> of every open notebook at <c>sections</c> level.</param>
    public GrantCommand(TextReader input, TextWriter output, bool interactive, Func<CancellationToken, Task<string>> readSections, SettingsWriter writer, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(readSections);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(clock);
        this.input = input;
        this.output = output;
        this.interactive = interactive;
        this.readSections = readSections;
        this.writer = writer;
        this.clock = clock;
    }

    /// <summary>Runs <c>grant</c> with the arguments after it. Returns the exit code: 0 done, 1 refused or failed, 2 bad usage.</summary>
    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!interactive)
        {
            output.WriteLine("OneNoteMcp grant needs an interactive terminal, so a program can't grant on your behalf. Run it yourself in a terminal.");
            return 1;
        }

        var revoke = false;
        var readSection = false;
        string? name = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--revoke":
                    revoke = true;
                    break;
                case "--read-section":
                    readSection = true;
                    if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        name = args[++i];
                    }

                    break;
                default:
                    output.WriteLine($"Unknown argument: {args[i]}");
                    output.WriteLine(Usage);
                    return 2;
            }
        }

        if (!readSection)
        {
            output.WriteLine("Only --read-section grants exist in this release.");
            output.WriteLine(Usage);
            return 2;
        }

        name ??= Ask("Section name: ");
        if (string.IsNullOrWhiteSpace(name))
        {
            output.WriteLine("No section name given; nothing changed.");
            return 1;
        }

        try
        {
            return revoke ? await RevokeAsync(name.Trim(), ct).ConfigureAwait(false) : await GrantAsync(name.Trim(), ct).ConfigureAwait(false);
        }
        catch (OneNoteException ex)
        {
            output.WriteLine($"{ex.Message} Nothing changed.");
            if (ex.UserAction is { } action)
            {
                output.WriteLine(action);
            }

            return 1;
        }
    }

    private async Task<int> GrantAsync(string name, CancellationToken ct)
    {
        var sections = await ProtectedSectionsAsync(ct).ConfigureAwait(false);
        var matches = sections.Where(s => GrantKeys.NameMatches(name, s.Name)).ToList();
        if (matches.Count == 0)
        {
            output.WriteLine("No password-protected section with that name is open in OneNote. Check the name, and that its notebook is open.");
            return 1;
        }

        var section = Pick(matches, s => Describe(s.Name, s.Location));
        if (section is null)
        {
            output.WriteLine("Nothing changed.");
            return 1;
        }

        if (GrantKeys.SectionKey(section.Path).Length == 0)
        {
            output.WriteLine("OneNote reports no path for that section, so no grant can be made for it. Nothing changed.");
            return 1;
        }

        output.WriteLine("Allow reading it for:");
        output.WriteLine("  1) a number of hours");
        output.WriteLine("  2) until you revoke it");
        var choice = Ask("Choose 1 or 2: ")?.Trim();
        DateTimeOffset? expires;
        var now = clock.GetUtcNow();
        switch (choice)
        {
            case "1":
                var hoursText = Ask(string.Create(CultureInfo.InvariantCulture, $"Hours ({ReadGrantLifetimes.MinHours}-{ReadGrantLifetimes.MaxHours}, default {ReadGrantLifetimes.DefaultHours}): "))?.Trim();
                var hours = ReadGrantLifetimes.DefaultHours;
                if (!string.IsNullOrEmpty(hoursText)
                    && (!int.TryParse(hoursText, NumberStyles.None, CultureInfo.InvariantCulture, out hours) || hours < ReadGrantLifetimes.MinHours || hours > ReadGrantLifetimes.MaxHours))
                {
                    output.WriteLine($"Hours must be a whole number from {ReadGrantLifetimes.MinHours} to {ReadGrantLifetimes.MaxHours}. Nothing changed.");
                    return 1;
                }

                expires = now.AddHours(hours);
                break;
            case "2":
                expires = null;
                break;
            default:
                output.WriteLine("Nothing changed.");
                return 1;
        }

        writer.SaveReadGrant(new ProtectedSectionGrant(
            GrantKeys.SectionKey(section.Path),
            section.Name,
            section.NotebookPath.Length == 0 ? string.Empty : GrantKeys.NotebookKey(section.NotebookPath),
            section.Id,
            now,
            GrantedVia.Cli,
            expires));
        output.WriteLine(expires is { } end
            ? $"Granted: \"{section.Name}\" can be read until {SettingsWriter.Format(end)} (UTC)."
            : $"Granted: \"{section.Name}\" can be read until you revoke it with: OneNoteMcp grant --revoke --read-section \"{section.Name}\"");
        return 0;
    }

    private async Task<int> RevokeAsync(string name, CancellationToken ct)
    {
        var snapshot = writer.Store.Read();
        if (snapshot.Status is SettingsStatus.Invalid or SettingsStatus.NewerSchema && !snapshot.DuplicateGrantKeysOnly)
        {
            snapshot.EnsureWritable();
        }

        // The section's current name when it is open; its stored name otherwise.
        IReadOnlyList<EncryptedSection> open;
        try
        {
            open = await ProtectedSectionsAsync(ct).ConfigureAwait(false);
        }
        catch (OneNoteException ex)
        {
            output.WriteLine($"Couldn't read the open notebooks ({ex.Message}); matching stored names only.");
            open = [];
        }

        var candidates = snapshot.ProtectedSectionEntries
            .GroupBy(g => g.Key, GrantKeys.Comparer)
            .Select(g => (Grant: g.First(), Live: open.FirstOrDefault(s => GrantKeys.Comparer.Equals(GrantKeys.SectionKey(s.Path), g.Key))))
            .Where(c => GrantKeys.NameMatches(name, c.Live?.Name) || GrantKeys.NameMatches(name, c.Grant.Name))
            .ToList();
        if (candidates.Count == 0)
        {
            output.WriteLine("No stored read grant matches that section name. Nothing changed.");
            return 1;
        }

        var chosen = Pick(candidates, c => c.Live is { } live ? Describe(live.Name, live.Location) : $"{c.Grant.Name} (not open; {c.Grant.Key})");
        if (chosen.Grant is null)
        {
            output.WriteLine("Nothing changed.");
            return 1;
        }

        var removed = writer.RevokeReadGrant(chosen.Grant.Key);
        output.WriteLine(removed > 0 ? $"Revoked the read grant for \"{chosen.Live?.Name ?? chosen.Grant.Name}\"." : "That grant was already gone.");
        return 0;
    }

    private async Task<IReadOnlyList<EncryptedSection>> ProtectedSectionsAsync(CancellationToken ct)
    {
        var xml = await readSections(ct).ConfigureAwait(false);
        try
        {
            return EncryptedSections.Find(xml);
        }
        catch (OneNoteXmlException ex)
        {
            throw new OneNoteException(ErrorCode.InvalidXml, $"GetHierarchy returned XML the server can't read: {ex.Message}", innerException: ex);
        }
    }

    /// <summary>The only match, or the one the user picks from a numbered list; default when they pick none.</summary>
    private T? Pick<T>(IReadOnlyList<T> matches, Func<T, string> describe)
    {
        if (matches.Count == 1)
        {
            return matches[0];
        }

        output.WriteLine("Several sections match:");
        for (var i = 0; i < matches.Count; i++)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {i + 1}) {describe(matches[i])}"));
        }

        var answer = Ask($"Choose 1 to {matches.Count}: ")?.Trim();
        return int.TryParse(answer, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= matches.Count ? matches[n - 1] : default;
    }

    private string? Ask(string prompt)
    {
        output.Write(prompt);
        output.Flush();
        return input.ReadLine();
    }

    private static string Describe(string name, IReadOnlyList<string> location) =>
        location.Count == 0 ? name : $"{string.Join(" > ", location)} > {name}";

}
