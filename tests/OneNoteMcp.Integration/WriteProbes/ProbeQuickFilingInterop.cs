using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>Spike 4.13: OneNote's <c>HierarchyElement</c> (type library values), for the picker's tree depth and buttons.</summary>
public enum ProbeHierarchyElement
{
    heNone = 0,
    heNotebooks = 1,
    heSectionGroups = 2,
    heSections = 4,
    hePages = 8,
}

/// <summary>
/// Spike 4.13: the spike's own early-bound copy of OneNote's <c>IQuickFilingDialog</c> (dual). Unlike 4.12's window
/// copies it is public, with <see cref="IProbeQuickFilingDialogCallback"/> and <see cref="FilingRecorder"/>: the
/// runtime exposes a managed object to COM only through public interfaces, and a unit test found the callback's CCW
/// answering <c>E_NOINTERFACE</c> while they were internal (<c>ProbeHelpersTests</c>). <c>IApplication.QuickFiling</c> (slot 27, <c>src/OneNoteMcp.Com/Interop/IApplication.cs</c>) returns it
/// as <c>IDispatch</c>, and the probe casts (QI) to this copy. Slot order is the type library's (comtypes module
/// generated on this machine, read 2026-11-17), slots 7–29.
/// <para>
/// The picker is configured only through <c>Title</c>, <c>Description</c>, <c>TreeDepth</c>, <c>ParentWindowHandle</c>
/// and <c>AddButton</c>, run with <c>Run</c>, and read through <c>SelectedItem</c>, <c>PressedButton</c> and
/// <c>WindowHandle</c> (plan, Always). Every other member is declared only as a placeholder that keeps the vtable in
/// order and can't be called: each is <see cref="ObsoleteAttribute"/> with <c>error: true</c>, so a call doesn't compile.
/// </para>
/// </summary>
[ComImport]
[Guid("1D12BD3F-89B6-4077-AA2C-C9DC2BCA42F9")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IProbeQuickFilingDialog
{
    // slots 7/8: get/put Title
    string Title { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }

    // slots 9/10: get/put Description
    string Description { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }

    // slot 11: get CheckboxText
    [Obsolete("Spike 4.13: the picker's checkbox is not used.", error: true)]
    void GetCheckboxTextPlaceholder();

    // slot 12: put CheckboxText
    [Obsolete("Spike 4.13: the picker's checkbox is not used.", error: true)]
    void SetCheckboxTextPlaceholder();

    // slot 13: get CheckboxState
    [Obsolete("Spike 4.13: the picker's checkbox is not used.", error: true)]
    void GetCheckboxStatePlaceholder();

    // slot 14: put CheckboxState
    [Obsolete("Spike 4.13: the picker's checkbox is not used.", error: true)]
    void SetCheckboxStatePlaceholder();

    // slot 15: get WindowHandle (the picker's own HWND, as an unsigned 64-bit value). Read once after Run, no title.
    ulong WindowHandle { get; }

    // slots 16/17: get/put TreeDepth
    ProbeHierarchyElement TreeDepth { get; set; }

    // slots 18/19: get/put ParentWindowHandle
    ulong ParentWindowHandle { get; set; }

    // slot 20: get Position (POINT)
    [Obsolete("Spike 4.13: the picker's position is not used.", error: true)]
    void GetPositionPlaceholder();

    // slot 21: put Position (POINT)
    [Obsolete("Spike 4.13: the picker's position is not used.", error: true)]
    void SetPositionPlaceholder();

    // slot 22: SetRecentResults(RecentResultType, bool, bool, bool)
    [Obsolete("Spike 4.13: SetRecentResults is not used.", error: true)]
    void SetRecentResultsPlaceholder();

    // slot 23: AddButton(bstr, HierarchyElement allowed, HierarchyElement allowedReadOnly, bool default)
    void AddButton(
        [MarshalAs(UnmanagedType.BStr)] string bstrText,
        ProbeHierarchyElement allowedElements,
        ProbeHierarchyElement allowedReadOnlyElements,
        [MarshalAs(UnmanagedType.VariantBool)] bool fDefault);

    // slot 24: Run(IQuickFilingDialogCallback)
    void Run([MarshalAs(UnmanagedType.Interface)] IProbeQuickFilingDialogCallback piCallback);

    // slot 25: get SelectedItem
    string SelectedItem { [return: MarshalAs(UnmanagedType.BStr)] get; }

    // slot 26: get PressedButton
    uint PressedButton { get; }

    // slot 27: put TreeCollapsedState
    [Obsolete("Spike 4.13: TreeCollapsedState is not used.", error: true)]
    void SetTreeCollapsedStatePlaceholder();

    // slot 28: put NotebookFilterOut
    [Obsolete("Spike 4.13: NotebookFilterOut is not used.", error: true)]
    void SetNotebookFilterOutPlaceholder();

    // slot 29: AddInitialEditor(bstr)
    [Obsolete("Spike 4.13: AddInitialEditor is not used.", error: true)]
    void AddInitialEditorPlaceholder();
}

/// <summary>Spike 4.13: OneNote's <c>IQuickFilingDialogCallback</c> (dual): slot 7 <c>OnDialogClosed</c>. The probe implements it (<see cref="FilingRecorder"/>).</summary>
[ComImport]
[Guid("627EA7B4-95B5-4980-84C1-9D20DA4460B1")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IProbeQuickFilingDialogCallback
{
    // slot 7
    void OnDialogClosed([MarshalAs(UnmanagedType.Interface)] IProbeQuickFilingDialog dialog);
}

/// <summary>
/// Spike 4.13: what one <c>OnDialogClosed</c> call saw. <see cref="Timestamp"/> is a <see cref="Stopwatch"/> timestamp,
/// so rows can give it relative to <c>Run</c>. A getter that failed keeps its HRESULT or exception type in
/// <see cref="ProbeValue.HResult"/>.
/// </summary>
internal sealed record FilingCall(int Sequence, long Timestamp, int ThreadId, string Apartment, bool DialogNull, ProbeValue SelectedItem, ProbeValue PressedButton);

/// <summary>
/// Spike 4.13: where a <see cref="FilingRecorder"/> puts its calls, held apart from the recorder so F7 can drop the
/// callback object and still see whether a call arrives. It holds no reference to the recorder.
/// </summary>
internal sealed class FilingCalls
{
    private readonly ConcurrentQueue<FilingCall> calls = new();
    private readonly TaskCompletionSource<FilingCall> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int sequence;

    /// <summary>Completes with the first call (continuations never run on OneNote's calling thread).</summary>
    public Task<FilingCall> First => first.Task;

    public IReadOnlyList<FilingCall> All => [.. calls];

    internal int NextSequence() => Interlocked.Increment(ref sequence);

    internal void Add(FilingCall call)
    {
        calls.Enqueue(call);
        first.TrySetResult(call);
    }
}

/// <summary>
/// Spike 4.13: the probe's <c>IQuickFilingDialogCallback</c>. It only records (time, managed thread ID, apartment,
/// <c>SelectedItem</c>, <c>PressedButton</c>, each read in its own try) and signals <see cref="Calls"/>, then returns at
/// once: OneNote's UI thread waits for the callback (OneMore, research row 26), so it never throws and never blocks. It
/// keeps no reference to the dialog it is given.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class FilingRecorder : IProbeQuickFilingDialogCallback
{
    internal FilingRecorder(FilingCalls calls) => Calls = calls;

    internal FilingCalls Calls { get; }

    public void OnDialogClosed(IProbeQuickFilingDialog dialog)
    {
        try
        {
            var timestamp = Stopwatch.GetTimestamp();
            var sequence = Calls.NextSequence();
            var thread = Environment.CurrentManagedThreadId;
            string apartment;
            try
            {
                apartment = Thread.CurrentThread.GetApartmentState().ToString();
            }
            catch (Exception ex)
            {
                apartment = ex.GetType().Name;
            }

            var selected = dialog is null ? ProbeValue.None : Read(() => dialog.SelectedItem);
            var pressed = dialog is null ? ProbeValue.None : Read(() => dialog.PressedButton);
            Calls.Add(new FilingCall(sequence, timestamp, thread, apartment, dialog is null, selected, pressed));
        }
        catch (Exception)
        {
            // Never rethrow into OneNote: its UI thread is waiting on this call. A failure here shows as no call recorded.
        }
    }

    /// <summary>One getter: its value, or the HRESULT it failed with (plus the exception type when it isn't a COMException). Never throws.</summary>
    internal static ProbeValue Read<T>(Func<T> getter)
    {
        try
        {
            return ProbeValue.Ok(getter());
        }
        catch (COMException ex)
        {
            return ProbeValue.Error(ex.HResult);
        }
        catch (Exception ex)
        {
            // Not a COMException (E_INVALIDARG, E_NOTIMPL and others arrive as other types): keep its HRESULT and type.
            return new ProbeValue(null, ProbeValue.HResultText(ex.HResult) + " " + ex.GetType().Name);
        }
    }
}
