using System.Globalization;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Server.Cli;

/// <summary>
/// <c>OneNoteMcp grant</c> (AD-6, AD-21): the interactive command through which the user grants a write to a notebook,
/// or grants or revokes a read of a password-protected section. It refuses to run without an interactive terminal, so a
/// model can't drive it.
/// <list type="bullet">
/// <item><c>grant --write [name]</c>: the typed name is matched, trimmed and ignoring case, against each open notebook's
/// displayed name (<c>nickname</c>, else <c>name</c>). Several matches are listed with their paths, and the user picks
/// one. The match is shown with its path, and the grant is made only when the user types <c>yes</c>. It is keyed by the
/// notebook's live <c>path</c> through <see cref="GrantKeys.NotebookKey"/> and keeps an existing permanent-delete
/// grant.</item>
/// <item><c>grant --revoke --write [name]</c>: removes a stored write grant, matched against the stored grants by the
/// notebook's live displayed name when it is open, else its stored name. Several matches are listed with their paths;
/// no <c>yes</c> is asked.</item>
/// <item><c>grant --read-section [name]</c>: the typed name is matched, trimmed and ignoring case, against the protected
/// sections of the open notebooks. Several matches are listed with their notebook and section groups, and the user picks
/// one. It offers <c>hours</c> (1 to 168, default 8) and <c>untilRevoked</c>; <c>once</c> exists only in the dialog.</item>
/// <item><c>grant --revoke --read-section [name]</c>: removes a stored read grant, matched the same way against the
/// stored grants (by the section's current name when it is open, else its stored name).</item>
/// </list>
/// Both revokes also run on a <c>settings.json</c> that is invalid only because two grant keys are equal, exactly or
/// ignoring case, and remove every entry equal to the chosen key, so the user can fix the file (AD-6, 2.3 plan Q7).
/// The grant is keyed by the section's live <c>path</c> through <see cref="GrantKeys.SectionKey"/>, and written by
/// <see cref="SettingsWriter"/>, which never holds the settings lock while the user types.
/// </summary>
public sealed class GrantCommand
{
    public const string Usage =
        "Usage:\n"
        + "  OneNoteMcp grant --write [\"<notebook name>\"]                  allow writing to a notebook\n"
        + "  OneNoteMcp grant --revoke --write [\"<notebook name>\"]         remove that permission\n"
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
        var write = false;
        string? name = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--revoke":
                    revoke = true;
                    break;
                case "--read-section" or "--write":
                    readSection |= args[i] == "--read-section";
                    write |= args[i] == "--write";
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

        if (readSection == write)
        {
            output.WriteLine("Give exactly one of --write or --read-section.");
            output.WriteLine(Usage);
            return 2;
        }

        name ??= Ask(write ? "Notebook name: " : "Section name: ");
        if (string.IsNullOrWhiteSpace(name))
        {
            output.WriteLine(write ? "No notebook name given; nothing changed." : "No section name given; nothing changed.");
            return 1;
        }

        try
        {
            return (write, revoke) switch
            {
                (true, true) => await RevokeWriteAsync(name.Trim(), ct).ConfigureAwait(false),
                (true, false) => await GrantWriteAsync(name.Trim(), ct).ConfigureAwait(false),
                (false, true) => await RevokeAsync(name.Trim(), ct).ConfigureAwait(false),
                _ => await GrantAsync(name.Trim(), ct).ConfigureAwait(false),
            };
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

        if (section.Key.Length == 0)
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

        writer.SaveReadGrant(ProtectedSectionGrant.Create(section.Key, section.Name, section.Id, section.NotebookPath, now, GrantedVia.Cli, expires));
        output.WriteLine(expires is { } end
            ? $"Granted: \"{section.Name}\" can be read until {SettingsWriter.Format(end)} (UTC)."
            : $"Granted: \"{section.Name}\" can be read until you revoke it with: OneNoteMcp grant --revoke --read-section \"<section name>\"");
        return 0;
    }

    /// <summary>
    /// <c>grant --write</c> (AD-6): the typed name is matched against each open notebook's displayed name. The match is
    /// shown with its path, and the grant is made only when the user types <c>yes</c> (Phill, 2026-11-10, 2.4 plan Q4),
    /// since a name passed as an argument may have been pasted from the model's suggestion.
    /// </summary>
    private async Task<int> GrantWriteAsync(string name, CancellationToken ct)
    {
        var notebooks = OpenNotebooks.Find(await readSections(ct).ConfigureAwait(false));
        var matches = notebooks.Where(n => GrantKeys.NameMatches(name, n.DisplayName)).ToList();
        if (matches.Count == 0)
        {
            output.WriteLine("No open notebook has that name. Check the name, and that the notebook is open in OneNote.");
            return 1;
        }

        var notebook = Pick(matches, n => $"{n.DisplayName} ({n.Path})", "notebooks");
        if (notebook is null)
        {
            output.WriteLine("Nothing changed.");
            return 1;
        }

        if (notebook.Key.Length == 0)
        {
            output.WriteLine("OneNote reports no path for that notebook, so no grant can be made for it. Nothing changed.");
            return 1;
        }

        output.WriteLine($"Notebook: {notebook.DisplayName}");
        output.WriteLine($"Path:     {notebook.Path}");
        output.WriteLine("Claude will be able to create and change pages in it, through OneNoteMcp.");
        if (!string.Equals(Ask("Type yes to allow it: ")?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("Nothing changed.");
            return 1;
        }

        writer.SaveWriteGrant(new NotebookGrant(notebook.Key, notebook.DisplayName, notebook.Id, Write: true, PermanentDelete: false, clock.GetUtcNow(), GrantedVia.Cli));
        output.WriteLine($"Granted: OneNoteMcp can now write to \"{notebook.DisplayName}\", until you revoke it with: OneNoteMcp grant --revoke --write \"<notebook name>\"");
        return 0;
    }

    /// <summary>
    /// <c>grant --revoke --write</c> (AD-6): the typed name is matched against the stored write grants, by the notebook's
    /// live displayed name when an open notebook has the grant's key, else by the stored name. Several matches are listed
    /// with their paths and the user picks one. No <c>yes</c> is asked: revoking only takes a permission away. It removes
    /// every entry equal to the key, exact or case-only duplicates included, and runs on a file invalid only for them.
    /// </summary>
    private async Task<int> RevokeWriteAsync(string name, CancellationToken ct)
    {
        var snapshot = ReadForRevoke();

        // The notebook's current displayed name when it is open; its stored name otherwise.
        IReadOnlyList<OpenNotebook> open;
        try
        {
            open = OpenNotebooks.Find(await readSections(ct).ConfigureAwait(false));
        }
        catch (OneNoteException ex)
        {
            output.WriteLine($"Couldn't read the open notebooks ({ex.Message}); matching stored names only.");
            open = [];
        }

        // Only entries that grant writes: one kept for its permanent-delete grant alone (epic 5) isn't a write grant.
        var candidates = snapshot.NotebookEntries
            .Where(g => g.Write)
            .GroupBy(g => g.Key, GrantKeys.Comparer)
            .Select(g => (Grant: g.First(), Live: open.FirstOrDefault(n => n.Key.Length > 0 && GrantKeys.Comparer.Equals(n.Key, g.Key))))
            .Where(c => GrantKeys.NameMatches(name, c.Live?.DisplayName) || GrantKeys.NameMatches(name, c.Grant.Name))
            .ToList();
        if (candidates.Count == 0)
        {
            output.WriteLine("No stored write grant matches that notebook name. Nothing changed.");
            return 1;
        }

        var chosen = Pick(candidates, c => c.Live is { } live ? $"{live.DisplayName} ({live.Path})" : $"{c.Grant.Name} (not open; {c.Grant.Key})", "notebooks");
        if (chosen.Grant is null)
        {
            output.WriteLine("Nothing changed.");
            return 1;
        }

        var removed = writer.RevokeWriteGrant(chosen.Grant.Key);
        output.WriteLine(removed > 0
            ? $"Revoked the write grant for \"{chosen.Live?.DisplayName ?? chosen.Grant.Name}\". The next write to it asks again."
            : "That grant was already gone.");
        return 0;
    }

    /// <summary>The settings for a revoke: a valid file, or one invalid only for duplicate grant keys (AD-6).</summary>
    private SettingsSnapshot ReadForRevoke()
    {
        var snapshot = writer.Store.Read();
        if (snapshot.Status is SettingsStatus.Invalid or SettingsStatus.NewerSchema && !snapshot.DuplicateGrantKeysOnly)
        {
            snapshot.EnsureWritable();
        }

        return snapshot;
    }

    private async Task<int> RevokeAsync(string name, CancellationToken ct)
    {
        var snapshot = ReadForRevoke();

        // The section's current name when it is open; its stored name otherwise.
        IReadOnlyList<ProtectedSection> open;
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
            .Select(g => (Grant: g.First(), Live: open.FirstOrDefault(s => GrantKeys.Comparer.Equals(s.Key, g.Key))))
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

    private async Task<IReadOnlyList<ProtectedSection>> ProtectedSectionsAsync(CancellationToken ct) =>
        ProtectedSections.Find(await readSections(ct).ConfigureAwait(false));

    /// <summary>The only match, or the one the user picks from a numbered list; default when they pick none.</summary>
    private T? Pick<T>(IReadOnlyList<T> matches, Func<T, string> describe, string what = "sections")
    {
        if (matches.Count == 1)
        {
            return matches[0];
        }

        output.WriteLine($"Several {what} match:");
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
