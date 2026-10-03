using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HyprWin.Core.Interop;

namespace HyprWin.Core;

/// <summary>
/// Data model for a single system tray notification icon.
/// </summary>
public sealed class TrayIconInfo
{
    public IntPtr OwnerHwnd { get; init; }
    public uint IconId { get; init; }
    public uint CallbackMessage { get; init; }
    public IntPtr IconHandle { get; init; }
    public string Tooltip { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public Guid GuidItem { get; init; }
    public ImageSource? IconImage { get; set; }
}

/// <summary>
/// Reads notification-area icons via the shell ITrayNotify callback (and Shell_NotifyIconGetRect
/// for rect lookup). Does not OpenProcess / ReadProcessMemory other processes.
/// Polls periodically and fires <see cref="IconsUpdated"/> with fresh icon data.
/// Click events can be forwarded to the original owner application.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    public event Action<List<TrayIconInfo>>? IconsUpdated;

    private System.Timers.Timer? _pollTimer;
    private bool _disposed;
    private volatile bool _paused;
    private bool _degraded;
    private string _degradedReason = "";

    private int _normalIntervalMs = 3000;
    private const int SlowIntervalMs = 10_000;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int NIN_SELECT = 0x0400;
    private const int WM_CONTEXTMENU = 0x007B;

    private readonly ConcurrentDictionary<string, TrayIconInfo> _icons = new();
    private TrayNotifySink? _sink;
    private object? _trayNotify;
    private ulong _callbackCookie;
    private bool _usingWin8Callback;

    public bool IsDegraded => _degraded;
    public string DegradedReason => _degradedReason;

    public void Start(int pollIntervalMs = 3000)
    {
        _normalIntervalMs = pollIntervalMs;
        TryRegisterTrayNotify();

        _pollTimer = new System.Timers.Timer(pollIntervalMs);
        _pollTimer.Elapsed += (_, _) => PollIcons();
        _pollTimer.Start();

        Task.Run(() => PollIcons());
    }

    /// <summary>
    /// Pause tray scanning when all top bars are hidden (fullscreen / gaming mode).
    /// Switches to a very slow 10 s interval instead of stopping entirely so that
    /// the icon list is still refreshed if the user alt-tabs out of the game.
    /// </summary>
    public void SetGamingMode(bool active)
    {
        if (_pollTimer == null) return;
        _paused = active;
        _pollTimer.Interval = active ? SlowIntervalMs : _normalIntervalMs;
        Logger.Instance.Info($"TrayIconService: GamingMode={active} (interval={_pollTimer.Interval} ms)");
    }

    private void TryRegisterTrayNotify()
    {
        try
        {
            var clsid = new Guid("25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD");
            var iidWin10 = new Guid("D133CE13-47E5-4A18-B4BE-638CCDE59460");
            var iidWin8 = new Guid("FB852B2C-6BAD-4605-9315-AAA3772D9F8E");

            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 0x17 /*CLSCTX_ALL*/, ref iidWin10, out object? obj);
            if (hr != 0 || obj == null)
            {
                hr = CoCreateInstance(ref clsid, IntPtr.Zero, 0x17, ref iidWin8, out obj);
                if (hr != 0 || obj == null)
                    throw new COMException("CoCreateInstance(CLSID_TrayNotify) failed", hr);
                _usingWin8Callback = true;
            }

            _trayNotify = obj;
            _sink = new TrayNotifySink(OnNotifyItem);

            if (_usingWin8Callback && obj is ITrayNotifyWin8 win8)
            {
                win8.RegisterCallback(_sink);
            }
            else if (obj is ITrayNotifyWin10 win10)
            {
                win10.RegisterCallback(_sink, out _callbackCookie);
            }
            else
            {
                // Dynamic fallback if the RCW type doesn't match our interfaces
                var type = obj.GetType();
                var register = type.GetMethod("RegisterCallback");
                if (register == null)
                    throw new InvalidOperationException("ITrayNotify.RegisterCallback not found");

                var args = register.GetParameters();
                if (args.Length == 2)
                {
                    object[] call = [_sink, 0UL];
                    register.Invoke(obj, call);
                    _callbackCookie = (ulong)call[1];
                }
                else
                {
                    register.Invoke(obj, [_sink]);
                    _usingWin8Callback = true;
                }
            }

            Logger.Instance.Info("TrayIconService: registered ITrayNotify callback (no cross-process memory reads)");
        }
        catch (Exception ex)
        {
            EnterDegradedMode(
                "ITrayNotify is unavailable; a full notification-area icon set requires Explorer COM that this session did not provide. " +
                $"Shell_NotifyIconGetRect alone cannot enumerate icons. Reason: {ex.Message}");
        }
    }

    private void EnterDegradedMode(string reason)
    {
        _degraded = true;
        _degradedReason = reason;
        Logger.Instance.Warn($"TrayIconService degraded: {reason}");
    }

    private void OnNotifyItem(uint eventId, NOTIFYITEM item)
    {
        string key = MakeKey(item.hwnd, item.uID, item.guidItem);
        const uint NIM_DELETE = 2;
        if (eventId == NIM_DELETE)
        {
            _icons.TryRemove(key, out _);
            return;
        }

        string processName = "";
        if (item.hwnd != IntPtr.Zero && NativeMethods.IsWindow(item.hwnd))
        {
            NativeMethods.GetWindowThreadProcessId(item.hwnd, out uint ownerPid);
            processName = NativeMethods.GetProcessName(ownerPid);
        }
        else if (!string.IsNullOrWhiteSpace(item.pszExeName))
        {
            processName = System.IO.Path.GetFileNameWithoutExtension(item.pszExeName);
        }

        string tooltip = item.pszTip ?? processName;
        if (string.IsNullOrWhiteSpace(tooltip))
            tooltip = processName;

        // Prefer a live rect from the documented Shell_NotifyIconGetRect API when identifiers exist.
        TryGetNotifyIconRect(item.hwnd, item.uID, item.guidItem);

        _icons[key] = new TrayIconInfo
        {
            OwnerHwnd = item.hwnd,
            IconId = item.uID,
            CallbackMessage = 0,
            IconHandle = item.hIcon,
            Tooltip = tooltip,
            ProcessName = processName,
            GuidItem = item.guidItem,
        };
    }

    private static string MakeKey(IntPtr hwnd, uint id, Guid guid)
        => guid != Guid.Empty ? guid.ToString() : $"{hwnd.ToInt64():X}-{id}";

    private void PollIcons()
    {
        if (_disposed) return;
        if (_paused) return;
        try
        {
            if (_degraded)
            {
                IconsUpdated?.Invoke(new List<TrayIconInfo>());
                return;
            }

            var snapshot = _icons.Values.ToList();
            IconsUpdated?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("Failed to poll tray icons", ex);
        }
    }

    /// <summary>
    /// Convert an HICON handle to a frozen WPF ImageSource.
    /// Must be called on the UI thread. Returns null on failure.
    /// </summary>
    public static ImageSource? IconToImageSource(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static void SendIconClick(TrayIconInfo icon, bool rightClick)
    {
        if (icon.OwnerHwnd == IntPtr.Zero) return;
        if (!NativeMethods.IsWindow(icon.OwnerHwnd)) return;

        try
        {
            int downMsg = rightClick ? WM_RBUTTONDOWN : WM_LBUTTONDOWN;
            int upMsg = rightClick ? WM_RBUTTONUP : WM_LBUTTONUP;
            uint callback = icon.CallbackMessage;

            if (callback != 0)
            {
                NativeMethods.PostMessage(icon.OwnerHwnd, callback, (IntPtr)icon.IconId, (IntPtr)downMsg);
                NativeMethods.PostMessage(icon.OwnerHwnd, callback, (IntPtr)icon.IconId, (IntPtr)upMsg);
                if (rightClick)
                    NativeMethods.PostMessage(icon.OwnerHwnd, callback, (IntPtr)icon.IconId, (IntPtr)WM_CONTEXTMENU);
                else
                    NativeMethods.PostMessage(icon.OwnerHwnd, callback, (IntPtr)icon.IconId, (IntPtr)NIN_SELECT);
            }
            else
            {
                NativeMethods.PostMessage(icon.OwnerHwnd, (uint)downMsg, IntPtr.Zero, IntPtr.Zero);
                NativeMethods.PostMessage(icon.OwnerHwnd, (uint)upMsg, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to forward tray click to {icon.ProcessName}", ex);
        }
    }

    public static void SendIconDoubleClick(TrayIconInfo icon)
    {
        if (icon.OwnerHwnd == IntPtr.Zero) return;
        if (!NativeMethods.IsWindow(icon.OwnerHwnd)) return;

        try
        {
            if (icon.CallbackMessage != 0)
            {
                NativeMethods.PostMessage(icon.OwnerHwnd, icon.CallbackMessage,
                    (IntPtr)icon.IconId, (IntPtr)WM_LBUTTONDBLCLK);
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to forward tray double-click to {icon.ProcessName}", ex);
        }
    }

    private static void TryGetNotifyIconRect(IntPtr hwnd, uint id, Guid guid)
    {
        var ident = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = hwnd,
            uID = id,
            guidItem = guid,
        };
        _ = Shell_NotifyIconGetRect(ref ident, out _);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer?.Stop();
        _pollTimer?.Dispose();

        try
        {
            if (_trayNotify is ITrayNotifyWin10 win10 && _callbackCookie != 0)
                win10.UnregisterCallback(_callbackCookie);
            else if (_trayNotify is ITrayNotifyWin8 win8 && _sink != null)
                win8.UnregisterCallback(_sink);
        }
        catch { /* explorer may already be gone */ }

        if (_trayNotify != null && Marshal.IsComObject(_trayNotify))
            Marshal.ReleaseComObject(_trayNotify);

        _trayNotify = null;
        _sink = null;
    }

    private sealed class TrayNotifySink : INotificationCB
    {
        private readonly Action<uint, NOTIFYITEM> _onNotify;
        public TrayNotifySink(Action<uint, NOTIFYITEM> onNotify) => _onNotify = onNotify;

        public void Notify(uint eventId, ref NOTIFYITEM notifyItem)
        {
            try { _onNotify(eventId, notifyItem); }
            catch { /* never throw back into explorer */ }
        }
    }

    [ComImport]
    [Guid("D782CCBA-AFB0-43F1-94DB-FDA377B8AA1D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INotificationCB
    {
        void Notify(uint eventId, [In] ref NOTIFYITEM notifyItem);
    }

    [ComImport]
    [Guid("D133CE13-47E5-4A18-B4BE-638CCDE59460")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITrayNotifyWin10
    {
        void RegisterCallback(INotificationCB callback, out ulong handle);
        void UnregisterCallback(ulong handle);
        void SetPreference(ref NOTIFYITEM notifyItem);
        void EnableAutoTray([MarshalAs(UnmanagedType.Bool)] bool enable);
        void DoAction([MarshalAs(UnmanagedType.Bool)] bool enable);
    }

    [ComImport]
    [Guid("FB852B2C-6BAD-4605-9315-AAA3772D9F8E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITrayNotifyWin8
    {
        void RegisterCallback(INotificationCB callback);
        void UnregisterCallback(INotificationCB callback);
        void SetPreference(ref NOTIFYITEM notifyItem);
        void EnableAutoTray([MarshalAs(UnmanagedType.Bool)] bool enable);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYITEM
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszExeName;
        [MarshalAs(UnmanagedType.LPWStr)] public string pszTip;
        public IntPtr hIcon;
        public IntPtr hwnd;
        public uint dwState;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out NativeMethods.RECT iconLocation);
}
