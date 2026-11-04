using System.Diagnostics;

namespace OneNoteMcp.Com;

/// <summary>Tells the gateway whether OneNote desktop is running, so it never activates OneNote silently (AD-3).</summary>
public interface IProcessProbe
{
    bool IsOneNoteRunning();
}

/// <summary>
/// Looks for an <c>ONENOTE.EXE</c> process in the current Windows session only. Another user's OneNote
/// (in another session) doesn't count, because COM activation from here would start a new one in ours.
/// </summary>
public sealed class ProcessProbe : IProcessProbe
{
    public bool IsOneNoteRunning()
    {
        int sessionId;
        using (var current = Process.GetCurrentProcess())
        {
            sessionId = current.SessionId;
        }

        var found = false;
        foreach (var process in Process.GetProcessesByName("ONENOTE"))
        {
            using (process)
            {
                try
                {
                    found |= process.SessionId == sessionId;
                }
                catch (InvalidOperationException)
                {
                    // The process exited while we looked.
                }
            }
        }

        return found;
    }
}
