using System.Runtime.InteropServices;
using SysWidge.Interop;

namespace SysWidge.Ui;

/// <summary>
/// A tray icon registered through Shell_NotifyIcon with a STABLE GUID, so Windows 11
/// treats it as the same icon across rebuilds/updates and the user's "show in taskbar"
/// (un-hide-from-overflow) choice sticks. WinForms' <see cref="NotifyIcon"/> can't set a
/// GUID, which is why every fresh binary kept reverting to the overflow flyout — Windows
/// keyed the promotion on an identity that changed when the .exe changed.
///
/// Defensive: if the GUID add fails (typically because the GUID is bound to a different
/// .exe path, or a stale registration lingers), it clears and retries, then falls back to
/// the classic (hWnd,uID) identity so the icon ALWAYS appears — only the cross-update
/// persistence needs the stable install path.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = NativeMethods.WM_APP + 0x101;

    private readonly ContextMenuStrip _menu;
    private readonly MessageWindow _window;
    private NativeMethods.NOTIFYICONDATAW _data;
    private bool _useGuid;
    private bool _added;

    public TrayIcon(Icon icon, string tooltip, ContextMenuStrip menu, Guid id)
    {
        _menu = menu;
        _window = new MessageWindow(this);

        _data = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _window.Handle,
            uID = 1,
            uCallbackMessage = CallbackMessage,
            hIcon = icon.Handle,
            szTip = tooltip ?? "",
            szInfo = "",
            szInfoTitle = "",
            guidItem = id,
        };

        Add();
    }

    private bool Send(uint msg) => NativeMethods.Shell_NotifyIconW(msg, ref _data);

    private void Add()
    {
        // Prefer the GUID identity (survives updates). If the add fails, clear a possibly
        // stale registration and retry once, then fall back to hWnd+uID so the icon shows.
        _data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON
                     | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP | NativeMethods.NIF_GUID;
        _useGuid = true;

        if (!Send(NativeMethods.NIM_ADD))
        {
            Send(NativeMethods.NIM_DELETE);              // drop a stale GUID registration
            if (!Send(NativeMethods.NIM_ADD))
            {
                _data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON
                             | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP;
                _useGuid = false;
                Send(NativeMethods.NIM_ADD);
            }
        }
        _added = true;

        // Opt into the modern (v4) callback semantics: right-click -> WM_CONTEXTMENU,
        // left-click -> NIN_SELECT, anchor point packed into wParam (screen coords).
        _data.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
        Send(NativeMethods.NIM_SETVERSION);
    }

    private void OnCallback(IntPtr wParam, IntPtr lParam)
    {
        int evt = unchecked((int)((long)lParam & 0xFFFF));      // v4: LOWORD(lParam) = event
        if (evt is NativeMethods.WM_CONTEXTMENU or NativeMethods.NIN_SELECT or NativeMethods.NIN_KEYSELECT)
        {
            long wp = (long)wParam;
            int x = unchecked((short)(wp & 0xFFFF));            // v4: anchor in screen coords
            int y = unchecked((short)((wp >> 16) & 0xFFFF));
            NativeMethods.SetForegroundWindow(_window.Handle);  // so the menu dismisses on outside click
            _menu.Show(x, y);
        }
    }

    /// <summary>Remove the icon from the tray (idempotent).</summary>
    public void Hide()
    {
        if (!_added) return;
        _data.uFlags = _useGuid ? NativeMethods.NIF_GUID : 0;
        Send(NativeMethods.NIM_DELETE);
        _added = false;
    }

    public void Dispose()
    {
        Hide();
        _window.DestroyHandle();
    }

    /// <summary>
    /// Hidden helper window that receives the tray callback message. A real (not
    /// message-only) window so SetForegroundWindow works for menu dismissal — the same
    /// approach WinForms' own NotifyIcon uses internally.
    /// </summary>
    private sealed class MessageWindow : NativeWindow
    {
        private readonly TrayIcon _owner;

        public MessageWindow(TrayIcon owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == CallbackMessage) { _owner.OnCallback(m.WParam, m.LParam); return; }
            base.WndProc(ref m);
        }
    }
}
