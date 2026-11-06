using System.Globalization;

namespace StressHarness;

/// <summary>Exit codes. Every message the harness prints is content-free: no names, titles, paths or query words.</summary>
internal static class Exit
{
    public const int Ok = 0;
    public const int Mismatches = 1;
    public const int Usage = 2;
    public const int UnexpectedTools = 3;
    public const int Precondition = 4;
    public const int Crashed = 5;
}

/// <summary>A refusal before any read: exit code plus a fixed, content-free message.</summary>
internal sealed class RefusalException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

internal sealed record Options
{
    public const string Usage =
        """
        usage: StressHarness (--notebook <ID> | --notebook-name <nickname>) --out <dir outside the repo> [options]
               StressHarness --fake-server <chars> [--fake-variant filler|structured|contract-no-readonly] [--fake-filler words|xml]

          --notebook <ID>           the notebook to test, by OneNote ID
          --notebook-name <name>    the notebook to test, by nickname or name (resolved here, never echoed)
          --out <dir>               where run.jsonl and summary.json go; must be outside the repo
          --exe <path>              the server (default: <repo>/dist/publish/OneNoteMcp.exe)
          --exe-arg <arg>           an argument for the server (repeatable; for testing the startup check)
          --root-pages              also read the root hierarchy at pages scope (every open notebook)
          --max-binaries <n>        binaries to compare (default 200)
          --queries <n>             find_pages queries (default 20)
          --seed <n>                random seed for queries and samples (default 1)
          --diag-page <pageId>      diagnose a get_page_content (basic) mismatch on one page (no --out)
          --diag-callbacks          with --notebook*: CallbackIDs by parent element and raw outcome (no --out)
          --probe-sync              with --notebook <ID> [--page <ID>]: SyncHierarchy image-download probe (no server)
          --fake-server <chars>     run as a one-tool MCP server returning <chars> (13 or more) of filler (the cap probe)
        """;

    public string? NotebookId { get; init; }

    public string? NotebookName { get; init; }

    public string? Out { get; init; }

    public string? Exe { get; init; }

    public IReadOnlyList<string> ExeArgs { get; init; } = [];

    public bool RootPages { get; init; }

    public int MaxBinaries { get; init; } = 200;

    public int Queries { get; init; } = 20;

    public int Seed { get; init; } = 1;

    public int? FakeServerChars { get; init; }

    public string FakeVariant { get; init; } = "filler";

    public string FakeFiller { get; init; } = "words";

    /// <summary>Diagnose one page's get_page_content (Basic) mismatch: prints positions, codepoints and element names only.</summary>
    public string? DiagPage { get; init; }

    /// <summary>Count the notebook's CallbackIDs by parent element name and raw binary outcome. Names of elements only.</summary>
    public bool DiagCallbacks { get; init; }

    /// <summary>The image-download probe: SyncHierarchy on one page (approved for notebook A only). Needs --notebook.</summary>
    public bool ProbeSync { get; init; }

    public string? ProbePage { get; init; }

    public bool IsDiag => DiagPage is not null || DiagCallbacks;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        var exeArgs = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next()
            {
                if (i + 1 >= args.Length)
                {
                    throw new RefusalException(Exit.Usage, "an option is missing its value");
                }

                return args[++i];
            }

            int NextInt(int min)
            {
                if (!int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < min)
                {
                    throw new RefusalException(Exit.Usage, "a numeric option has an invalid value");
                }

                return n;
            }

            o = a switch
            {
                "--notebook" => o with { NotebookId = Next() },
                "--notebook-name" => o with { NotebookName = Next() },
                "--out" => o with { Out = Next() },
                "--exe" => o with { Exe = Next() },
                "--exe-arg" => Add(o, exeArgs, Next()),
                "--root-pages" => o with { RootPages = true },
                "--max-binaries" => o with { MaxBinaries = NextInt(0) },
                "--queries" => o with { Queries = NextInt(0) },
                "--seed" => o with { Seed = NextInt(int.MinValue) },
                "--fake-server" => o with { FakeServerChars = NextInt(FakeServer.EndMarker.Length) },
                "--fake-filler" => o with { FakeFiller = Next() },
                "--diag-page" => o with { DiagPage = Next() },
                "--diag-callbacks" => o with { DiagCallbacks = true },
                "--probe-sync" => o with { ProbeSync = true },
                "--page" => o with { ProbePage = Next() },
                "--fake-variant" => o with { FakeVariant = Next() },
                // The unknown argument itself is never echoed: it could be a name typed in the wrong place.
                _ => throw new RefusalException(Exit.Usage, $"unknown argument at position {i + 1}"),
            };
        }

        o = o with { ExeArgs = exeArgs };
        if (o.FakeServerChars is not null)
        {
            if (o.FakeVariant is not ("filler" or "structured" or "contract-no-readonly"))
            {
                throw new RefusalException(Exit.Usage, "--fake-variant must be filler, structured or contract-no-readonly");
            }

            if (o.FakeFiller is not ("words" or "xml"))
            {
                throw new RefusalException(Exit.Usage, "--fake-filler must be words or xml");
            }

            return o;
        }

        if (o.DiagPage is not null)
        {
            if (o.DiagPage.Length == 0 || o.DiagCallbacks)
            {
                throw new RefusalException(Exit.Usage, "--diag-page takes a page ID and runs alone");
            }

            return o;
        }

        if (o.ProbeSync)
        {
            if (string.IsNullOrEmpty(o.NotebookId))
            {
                throw new RefusalException(Exit.Usage, "--probe-sync needs --notebook <ID>");
            }

            return o;
        }

        if (o.NotebookId is null == (o.NotebookName is null))
        {
            throw new RefusalException(Exit.Usage, "give exactly one of --notebook or --notebook-name");
        }

        if ((o.NotebookId ?? o.NotebookName)!.Length == 0)
        {
            throw new RefusalException(Exit.Usage, "the notebook option is empty");
        }

        if (!o.DiagCallbacks && string.IsNullOrWhiteSpace(o.Out))
        {
            throw new RefusalException(Exit.Usage, "--out is required");
        }

        return o;
    }

    private static Options Add(Options o, List<string> list, string value)
    {
        list.Add(value);
        return o;
    }
}

/// <summary>Finds the repo and enforces "--out must resolve outside the repo".</summary>
internal static class RepoPaths
{
    /// <summary>The repo roots above the harness's own folder and above the current directory.</summary>
    public static IReadOnlyList<string> Roots()
    {
        var roots = new List<string>();
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            if (Find(start) is { } root && !roots.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(root);
            }
        }

        return roots;
    }

    public static string? Find(string start)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OneNoteMcp.slnx")) || Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return Normalise(dir.FullName);
            }
        }

        return null;
    }

    public static bool IsInside(string path, string root)
    {
        var p = Normalise(path) + Path.DirectorySeparatorChar;
        var r = Normalise(root) + Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalise(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
}
