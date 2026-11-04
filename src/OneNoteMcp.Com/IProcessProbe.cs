using System.Diagnostics;

namespace OneNoteMcp.Com;

/// <summary>Tells the gateway whether OneNote desktop is running, so it never activates OneNote silently (AD-3).</summary>
public interface IProcessProbe
{
    bool IsOneNoteRunning();
}

/// <summary>Looks for an <c>ONENOTE.EXE</c> process in this session's process list.</summary>
public sealed class ProcessProbe : IProcessProbe
{
    public bool IsOneNoteRunning()
    {
        var processes = Process.GetProcessesByName("ONENOTE");
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }
}
