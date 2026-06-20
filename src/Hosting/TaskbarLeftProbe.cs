using System.Runtime.InteropServices;
using SysWidge.Interop;

namespace SysWidge.Hosting;

/// <summary>
/// Measures how far the taskbar's LEFT-docked content reaches — the Windows 11 Widgets
/// button (which includes a variable-width weather pill) and the optional Search box — so
/// the widget can dock just past it, and tighten to the left edge when nothing is there.
///
/// Win11 taskbar elements are XAML with no HWNDs, so geometry is only reachable through UI
/// Automation. We talk to UIA over its COM interface directly (a minimal hand-rolled vtable)
/// rather than the managed UIAutomationClient assemblies, to avoid dragging WPF into a
/// deliberately lean process. Every call is wrapped so any failure degrades to the caller's
/// static-offset fallback rather than disturbing the widget.
/// </summary>
internal static class TaskbarLeftProbe
{
    private const int TreeScope_Descendants = 4;
    private const int UIA_BoundingRectanglePropertyId = 30001;

    private static IUIAutomation? _automation;
    private static bool _comUnavailable;

    /// <summary>
    /// Right edge (physical px, relative to the taskbar's left) of the left-docked content,
    /// i.e. the clearance the widget should leave on the left. Returns 0 when nothing is
    /// docked left (dock tight), or -1 if UIA is unavailable (caller falls back).
    /// </summary>
    public static int Measure(IntPtr shellTray, NativeMethods.RECT tb)
    {
        try
        {
            var automation = GetAutomation();
            if (automation is null) return -1;

            var root = automation.ElementFromHandle(shellTray);
            if (root is null) return -1;

            IUIAutomationCondition? cond = null;
            IUIAutomationElementArray? all = null;
            try
            {
                cond = automation.CreateTrueCondition();
                all = root.FindAll(TreeScope_Descendants, cond);
                int count = all.Length;

                double tbLeft = tb.Left;
                double tbWidth = tb.Right - tb.Left;
                // A left-docked element STARTS within the left 30% and ENDS within the left
                // 45% — wide enough for the Widgets+weather pill, tight enough to exclude the
                // centered Start / running-apps cluster.
                double startMax = tbLeft + tbWidth * 0.30;
                double endMax = tbLeft + tbWidth * 0.45;
                double maxRight = tbLeft; // nothing on the left -> clearance 0

                for (int i = 0; i < count; i++)
                {
                    var el = all.GetElement(i);
                    try
                    {
                        if (el.GetCurrentPropertyValue(UIA_BoundingRectanglePropertyId) is double[] r
                            && r.Length == 4 && r[2] > 0)
                        {
                            double left = r[0];
                            double right = r[0] + r[2];   // UiaRect is {left, top, width, height}
                            if (left >= tbLeft - 2 && left <= startMax && right <= endMax && right > maxRight)
                                maxRight = right;
                        }
                    }
                    finally { Marshal.ReleaseComObject(el); }
                }

                int clearance = (int)Math.Round(maxRight - tbLeft);
                return clearance < 0 ? 0 : clearance;
            }
            finally
            {
                if (all is not null) Marshal.ReleaseComObject(all);
                if (cond is not null) Marshal.ReleaseComObject(cond);
                Marshal.ReleaseComObject(root);
            }
        }
        catch
        {
            return -1; // any UIA hiccup -> caller uses the static offset
        }
    }

    private static IUIAutomation? GetAutomation()
    {
        if (_automation is not null) return _automation;
        if (_comUnavailable) return null;
        try { _automation = (IUIAutomation)new CUIAutomation(); }
        catch { _comUnavailable = true; }
        return _automation;
    }

    // -------------------------------------------------- minimal UIA COM surface
    // Only the vtable slots we actually call are typed; the rest are placeholders that
    // preserve slot order. Do NOT call the placeholder methods.

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        [PreserveSig] int CompareElements(IUIAutomationElement a, IUIAutomationElement b, out int same);
        [PreserveSig] int CompareRuntimeIds(IntPtr a, IntPtr b, out int same);
        [PreserveSig] int GetRootElement(out IntPtr root);
        IUIAutomationElement ElementFromHandle(IntPtr hwnd);                  // slot 4
        [PreserveSig] int ElementFromPoint(long pt, out IntPtr el);
        [PreserveSig] int GetFocusedElement(out IntPtr el);
        [PreserveSig] int GetRootElementBuildCache(IntPtr cache, out IntPtr el);
        [PreserveSig] int ElementFromHandleBuildCache(IntPtr hwnd, IntPtr cache, out IntPtr el);
        [PreserveSig] int ElementFromPointBuildCache(long pt, IntPtr cache, out IntPtr el);
        [PreserveSig] int GetFocusedElementBuildCache(IntPtr cache, out IntPtr el);
        [PreserveSig] int CreateTreeWalker(IUIAutomationCondition c, out IntPtr walker);
        [PreserveSig] int get_ControlViewWalker(out IntPtr walker);
        [PreserveSig] int get_ContentViewWalker(out IntPtr walker);
        [PreserveSig] int get_RawViewWalker(out IntPtr walker);
        [PreserveSig] int get_RawViewCondition(out IntPtr cond);
        [PreserveSig] int get_ControlViewCondition(out IntPtr cond);
        [PreserveSig] int get_ContentViewCondition(out IntPtr cond);
        [PreserveSig] int CreateCacheRequest(out IntPtr cache);
        IUIAutomationCondition CreateTrueCondition();                        // slot 19
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int GetRuntimeId(out IntPtr runtimeId);
        [PreserveSig] int FindFirst(int scope, IUIAutomationCondition c, out IntPtr found);
        IUIAutomationElementArray FindAll(int scope, IUIAutomationCondition c);   // slot 4
        [PreserveSig] int FindFirstBuildCache(int scope, IUIAutomationCondition c, IntPtr cache, out IntPtr found);
        [PreserveSig] int FindAllBuildCache(int scope, IUIAutomationCondition c, IntPtr cache, out IntPtr found);
        [PreserveSig] int BuildUpdatedCache(IntPtr cache, out IntPtr updated);
        [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId); // slot 8
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int Length { get; }
        IUIAutomationElement GetElement(int index);
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }
}
