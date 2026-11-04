using System.Reflection;

namespace OneNoteMcp.Tests;

/// <summary>Source-tree paths, baked in at build time so tests work from any output folder (including --artifacts-path).</summary>
internal static class RepoPaths
{
    public static string Root { get; } = Path.GetFullPath(
        typeof(RepoPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RepoRoot").Value!);

    public static string Combine(params string[] parts) => Path.Combine([Root, .. parts]);
}
