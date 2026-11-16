using System.Diagnostics;
using OneNoteMcp.Com;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// The probes' crash breaker. Pins the <c>ONENOTE.EXE</c> processes running when the scenario starts and allows one
/// activation only (the session sets <c>idleReleaseSeconds</c> to a day, so a second one is always a reconnect).
/// <para>
/// Why: on 2026-11-10 a probe call crashed OneNote. The crashed process stayed alive while Windows Error Reporting
/// wrote its dump, so <see cref="ProcessProbe"/> still reported OneNote running, and the gateway's reconnect started a
/// hidden <c>ONENOTE.EXE -Embedding</c>. Here, a changed process set or a second activation trips the breaker: from
/// then on OneNote reads as not running (silent activation is off, so the gateway refuses every call). A tripped
/// integrity check lists no OneNote, so the gateway stops before <c>CoCreateInstance</c> with
/// <c>OneNoteNotRunning</c>, not <c>IntegrityMismatch</c> (backlog 4).
/// </para>
/// <para>
/// This probe compares process IDs only: it doesn't apply <see cref="ProcessProbe"/>'s Windows Error Reporting and
/// window checks. A crashed <c>ONENOTE.EXE</c> that is still listed therefore passes its running check, and the
/// breaker trips at the reconnect's integrity check instead.
/// </para>
/// </summary>
internal sealed class PinnedOneNoteProbe : IProcessProbe
{
    private readonly ProcessProbe inner = new();
    private readonly HashSet<int> pinned;
    private int activations;
    private volatile string? tripped;

    public PinnedOneNoteProbe()
    {
        pinned = OneNoteProcessIds();
    }

    public bool HasOneNote => pinned.Count > 0;

    /// <summary>Why the breaker tripped, or null.</summary>
    public string? Tripped => tripped;

    public void Trip(string reason) => Interlocked.CompareExchange(ref tripped, reason, null);

    public bool IsOneNoteRunning()
    {
        if (tripped is null && !OneNoteProcessIds().SetEquals(pinned))
        {
            Trip($"the ONENOTE.EXE processes changed (pinned [{string.Join(",", pinned)}])");
        }

        return tripped is null;
    }

    /// <summary>Runs just before <c>CoCreateInstance</c>: the second call is a reconnect, so it fails the check.</summary>
    public IntegrityReport CheckIntegrity()
    {
        if (Interlocked.Increment(ref activations) > 1)
        {
            Trip("the gateway tried to reconnect (OneNote likely crashed)");
        }

        // No OneNote listed reads as not running, so the gateway stops before CoCreateInstance. No server level keeps it
        // a mismatch even if a session ever turns silent activation on.
        return tripped is null ? inner.CheckIntegrity() : new IntegrityReport(null, []);
    }

    internal static HashSet<int> OneNoteProcessIds()
    {
        var processes = Process.GetProcessesByName("ONENOTE");
        try
        {
            return processes.Select(p => p.Id).ToHashSet();
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }
}
