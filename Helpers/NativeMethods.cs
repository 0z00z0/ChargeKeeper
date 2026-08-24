using System.Runtime.InteropServices;
using Windows.Graphics;

namespace ChargeKeeper.Helpers;

/// <summary>Thin wrappers around Win32 APIs used across the app.</summary>
internal static class NativeMethods
{
    private const uint SPI_GETWORKAREA = 0x0030;

    private const uint MONITOR_DEFAULTTONEAREST = 0x0002;
    private const int  MDT_EFFECTIVE_DPI        = 0;

    // ES_CONTINUOUS makes the request stick until cleared rather than resetting one idle timer. The
    // state is PER-THREAD: set and clear must happen on the same long-lived thread.
    internal const uint ES_CONTINUOUS       = 0x80000000;
    internal const uint ES_SYSTEM_REQUIRED  = 0x00000001;
    internal const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    internal static extern uint SetThreadExecutionState(uint esFlags);

    // SetThreadExecutionState cannot hold off a lid-close sleep: lid close is a power-policy action,
    // not an idle timeout. Delaying it means overriding the user's LIDACTION to "do nothing" and
    // putting it back afterwards.
    private static readonly Guid GUID_SUB_BUTTONS = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static readonly Guid GUID_LIDACTION   = new("5ca83367-6e45-459f-a27b-476b1d01c936");
    private static readonly Guid GUID_LIDSWITCH_STATE_CHANGE = new("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");

    /// <summary>LIDACTION index for "do nothing" — what the delay feature parks the setting on.</summary>
    internal const uint LIDACTION_DO_NOTHING = 0;

    private const uint DEVICE_NOTIFY_CALLBACK  = 0x00000002;
    private const uint PBT_POWERSETTINGCHANGE  = 0x8013;

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, IntPtr schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupGuid, ref Guid powerSettingGuid, out uint valueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupGuid, ref Guid powerSettingGuid, out uint valueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupGuid, ref Guid powerSettingGuid, uint valueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupGuid, ref Guid powerSettingGuid, uint valueIndex);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    // BOOLEAN is one byte, not the 4-byte BOOL the default marshaller would use.
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    /// <summary>Callback shape for <c>PowerSettingRegisterNotification</c> under DEVICE_NOTIFY_CALLBACK.</summary>
    private delegate uint DeviceNotifyCallback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    // The real tail is UCHAR Data[1]. The lid payload is a DWORD whose low byte carries the state,
    // and Windows is little-endian everywhere, so one byte is the whole answer.
    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingRegisterNotification(ref Guid settingGuid, uint flags,
        ref DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS recipient, out IntPtr registrationHandle);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingUnregisterNotification(IntPtr registrationHandle);

    // GetPwrCapabilities takes no buffer length and always writes the whole SYSTEM_POWER_CAPABILITIES,
    // so the buffer is deliberately oversized — a struct that grows in a later Windows build cannot
    // then overrun it. LidPresent is the third BOOLEAN in that struct, hence index 2.
    private const int LidPresentOffset = 2;

    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities(byte[] systemPowerCapabilities);

    /// <summary>
    /// Whether this machine has a lid, from the OS power capabilities — the same answer the Windows
    /// power UI uses to decide whether to offer a lid-close action at all. Null when the query fails,
    /// which the caller must not read as "no lid": a laptop losing the feature is the worse outcome.
    /// </summary>
    internal static bool? LidPresent()
    {
        var buffer = new byte[256];
        try { return GetPwrCapabilities(buffer) ? buffer[LidPresentOffset] != 0 : null; }
        catch { return null; }
    }

    // Rooted for the subscription's lifetime: the OS keeps a RAW function pointer to this delegate,
    // which the GC cannot see. Letting it be collected turns the next lid event into a hard crash.
    private static DeviceNotifyCallback? _lidCallback;

    /// <summary>Runs <paramref name="use"/> against the active power scheme's GUID, or returns
    /// <paramref name="fallback"/> when the scheme cannot be resolved. The GUID comes back in memory
    /// the caller must LocalFree, hence the wrapper rather than a bare call at each site.</summary>
    private static T WithActiveScheme<T>(Func<Guid, IntPtr, T> use, T fallback)
    {
        IntPtr scheme = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out scheme) != 0 || scheme == IntPtr.Zero) return fallback;
            return use(Marshal.PtrToStructure<Guid>(scheme), scheme);
        }
        catch { return fallback; }
        finally { if (scheme != IntPtr.Zero) LocalFree(scheme); }
    }

    /// <summary>
    /// The active scheme's GUID together with its AC and DC lid-close action indices (0 do nothing,
    /// 1 sleep, 2 hibernate, 3 shut down), or null when the query fails or the scheme carries no lid
    /// setting. The scheme comes back with the values because lid actions are PER-SCHEME and only
    /// mean anything together. Null must never be read as zero — the caller persists this to restore
    /// later, so a bogus zero would park the machine permanently on "do nothing".
    /// </summary>
    internal static (Guid Scheme, uint Ac, uint Dc)? ReadActiveLidCloseAction() =>
        WithActiveScheme<(Guid, uint, uint)?>((scheme, _) =>
        {
            var s = scheme; var sub = GUID_SUB_BUTTONS; var setting = GUID_LIDACTION;
            if (PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, out uint ac) != 0) return null;
            if (PowerReadDCValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, out uint dc) != 0) return null;
            return (scheme, ac, dc);
        }, null);

    /// <summary>
    /// Sets <paramref name="scheme"/>'s AC and DC lid-close action indices. Returns false if any step
    /// failed, in which case the caller must assume the scheme is in an unknown state and re-read it.
    /// Targets an explicit scheme, so a power-plan switch between capture and restore cannot write
    /// one plan's saved values into another. Writing the value is not enough — the scheme must be
    /// re-activated for the change to reach the running system, hence the closing PowerSetActiveScheme.
    /// </summary>
    internal static bool WriteLidCloseAction(Guid scheme, uint ac, uint dc) =>
        WithActiveScheme((_, activeRaw) =>
        {
            var s = scheme; var sub = GUID_SUB_BUTTONS; var setting = GUID_LIDACTION;
            if (PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, ac) != 0) return false;
            if (PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, dc) != 0) return false;
            return PowerSetActiveScheme(IntPtr.Zero, activeRaw) == 0;
        }, false);

    /// <summary>
    /// Subscribes to lid open/close, invoking <paramref name="onLidState"/> with true when the lid is
    /// CLOSED. Returns a registration handle for <see cref="UnregisterLidNotification"/>, or
    /// IntPtr.Zero if the subscription failed.
    /// <para>Windows invokes the callback once immediately with the current lid state, before any
    /// real transition — the caller must treat that first reading as a seed, not as a lid close.</para>
    /// <para>One subscription at a time: a second call is refused rather than overwriting
    /// <see cref="_lidCallback"/>, which would unroot the live delegate while the OS still holds its
    /// raw thunk.</para>
    /// </summary>
    internal static IntPtr RegisterLidNotification(Action<bool> onLidState)
    {
        if (_lidCallback is not null) return IntPtr.Zero;
        try
        {
            _lidCallback = (_, type, setting) =>
            {
                if (type == PBT_POWERSETTINGCHANGE && setting != IntPtr.Zero)
                {
                    var s = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(setting);
                    if (s.PowerSetting == GUID_LIDSWITCH_STATE_CHANGE && s.DataLength >= 1)
                        onLidState(s.Data == 0);   // 0 = closed, 1 = open
                }
                return 0;   // ERROR_SUCCESS
            };

            var recipient = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
            {
                Callback = Marshal.GetFunctionPointerForDelegate(_lidCallback),
                Context  = IntPtr.Zero,
            };
            var guid = GUID_LIDSWITCH_STATE_CHANGE;
            if (PowerSettingRegisterNotification(ref guid, DEVICE_NOTIFY_CALLBACK, ref recipient, out var handle) == 0)
                return handle;
        }
        catch { /* absent on older builds — the caller degrades to "no lid events" */ }

        _lidCallback = null;
        return IntPtr.Zero;
    }

    /// <summary>Ends a <see cref="RegisterLidNotification"/> subscription. Safe on IntPtr.Zero.</summary>
    internal static void UnregisterLidNotification(IntPtr registration)
    {
        if (registration == IntPtr.Zero) return;
        try { PowerSettingUnregisterNotification(registration); }
        catch { /* nothing useful to do while tearing down */ }
        _lidCallback = null;
    }

    /// <summary>Puts the machine into standby. An explicit suspend request, not a policy action, so it
    /// still works while the lid-close action is parked on "do nothing".</summary>
    internal static bool Suspend()
    {
        try { return SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false); }
        catch { return false; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    /// <summary>
    /// Locks the workstation, as Win+L does. Used at lid close while the lid-close delay holds the
    /// machine awake — the one window in which a shut lid no longer implies a sign-in prompt.
    /// </summary>
    internal static bool LockComputer()
    {
        try { return LockWorkStation(); }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int  cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out RECT output, uint winIni);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    // Windows 10 1607+ only; the call sites catch so older builds degrade rather than crash.
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>The system double-click interval. Read every time rather than cached — it is a user
    /// setting that changes without notifying us.</summary>
    internal static TimeSpan DoubleClickTime => TimeSpan.FromMilliseconds(GetDoubleClickTime());

    /// <summary>DPI of the monitor hosting the shell taskbar, which can differ from the process's own
    /// DPI context in a mixed-DPI multi-monitor setup. Falls back to the system DPI, then 96.</summary>
    internal static uint GetTaskbarDpi()
    {
        try
        {
            var hwnd = FindWindow("Shell_TrayWnd", null);
            if (hwnd != IntPtr.Zero)
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi != 0) return dpi;
            }
        }
        catch { /* absent pre-1607 */ }

        try
        {
            uint sys = GetDpiForSystem();
            if (sys != 0) return sys;
        }
        catch { /* absent pre-1607 */ }

        return 96; // 100 % DPI
    }

    /// <summary>Work area (physical px) and DPI scale of the monitor under the mouse cursor — the
    /// screen whose tray the user just clicked. Falls back to the primary monitor at 100 %.</summary>
    internal static (RECT WorkArea, double Scale) GetCursorMonitorMetrics()
    {
        if (GetCursorPos(out var cursor))
        {
            var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
            var info    = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

            if (GetMonitorInfo(monitor, ref info))
            {
                double scale = 1.0;
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX != 0)
                    scale = dpiX / 96.0;

                return (info.rcWork, scale);
            }
        }

        return (GetPrimaryWorkArea(), 1.0);
    }

    /// <summary>
    /// Opening rect (physical px) for a window of <paramref name="dipWidth"/> × <paramref name="dipHeight"/>
    /// DIPs, centred on the monitor under the cursor and capped to its work area. Takes DIPs rather
    /// than a caller-computed pixel size because size and position must come from the same monitor's
    /// metrics, or a mixed-DPI setup mis-sizes the window.
    /// </summary>
    internal static RectInt32 CentreRectOnCursorMonitor(int dipWidth, int dipHeight)
    {
        var (work, scale) = GetCursorMonitorMetrics();
        int workW = work.Right  - work.Left;
        int workH = work.Bottom - work.Top;
        return CentreInWorkArea(work,
                                Math.Min((int)Math.Round(dipWidth  * scale), workW),
                                Math.Min((int)Math.Round(dipHeight * scale), workH));
    }

    /// <summary>
    /// Centres an already-sized <paramref name="w"/> × <paramref name="h"/> rect (physical px) inside
    /// <paramref name="work"/>. Deliberately not clamped: a rect larger than the work area centres
    /// with symmetric overhang rather than being pinned to the top-left corner, which is what the
    /// callers that intentionally oversize want.
    /// </summary>
    internal static RectInt32 CentreInWorkArea(RECT work, int w, int h)
        => new(work.Left + (work.Right  - work.Left - w) / 2,
               work.Top  + (work.Bottom - work.Top  - h) / 2,
               w, h);

    /// <summary>Usable desktop area on the primary monitor (physical px). Falls back to a 1080p work
    /// area if the Win32 call fails.</summary>
    private static RECT GetPrimaryWorkArea()
    {
        if (SystemParametersInfo(SPI_GETWORKAREA, 0, out var rect, 0))
            return rect;

        return new() { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
    }

    /// <summary>
    /// Clamps a saved window rect (physical px) into the work area of the monitor nearest its centre,
    /// shrinking it if it is larger than that monitor, so a rect saved on a since-disconnected
    /// monitor is pulled back onto a connected one. Falls back to the input rect unchanged if the
    /// monitor query fails.
    /// </summary>
    internal static (int X, int Y, int W, int H) ClampRectToNearestMonitor(int x, int y, int w, int h)
        => WorkAreaForRect(x, y, w, h) is { } work
               ? WindowFit.Fit((x, y, w, h), requiredHeight: 0, work)
               : (x, y, w, h);

    /// <summary>Work area (physical px) of the monitor nearest the given rect's centre, or null if the
    /// monitor query fails.</summary>
    internal static (int X, int Y, int W, int H)? WorkAreaForRect(int x, int y, int w, int h)
    {
        var centre  = new POINT { X = x + w / 2, Y = y + h / 2 };
        var monitor = MonitorFromPoint(centre, MONITOR_DEFAULTTONEAREST);
        var info    = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            return null;

        var work = info.rcWork;
        return (work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }

    // uxtheme.dll exposes these only by ordinal — no named exports. 135 = SetPreferredAppMode
    // (Win10 1903+), 104 = RefreshImmersiveColorPolicyState.
    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = false)]
    private static extern int SetPreferredAppMode(int mode);   // 0=Default 1=AllowDark 2=ForceDark 3=ForceLight

    [DllImport("uxtheme.dll", EntryPoint = "#104", SetLastError = false)]
    private static extern void RefreshImmersiveColorPolicyState();

    /// <summary>Opts the process into dark-mode rendering for native Win32 UI (the tray context menu).
    /// Call once, before any UI is created, so the menu HWND inherits the setting.</summary>
    internal static void EnableDarkModeForNativeUi()
    {
        try
        {
            SetPreferredAppMode(1); // AllowDark — follows the system light/dark preference
            RefreshImmersiveColorPolicyState();
        }
        catch { /* ordinal absent on old builds — non-fatal */ }
    }

    // Plain Win32 MessageBox: callable from any thread, works in this elevated unpackaged app, and
    // needs no WinUI XamlRoot.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK              = 0x00000000;
    private const uint MB_YESNO           = 0x00000004;
    private const uint MB_ICONERROR       = 0x00000010;
    private const uint MB_ICONWARNING     = 0x00000030;
    private const uint MB_ICONINFORMATION = 0x00000040;
    private const int  IDYES              = 6;

    internal static void Info(string text, string caption)
        => MessageBoxW(IntPtr.Zero, text, caption, MB_OK | MB_ICONINFORMATION);

    internal static void Warn(string text, string caption)
        => MessageBoxW(IntPtr.Zero, text, caption, MB_OK | MB_ICONWARNING);

    internal static void Error(string text, string caption)
        => MessageBoxW(IntPtr.Zero, text, caption, MB_OK | MB_ICONERROR);

    /// <summary>Yes/No prompt; returns true when the user clicks Yes.</summary>
    internal static bool Confirm(string text, string caption)
        => MessageBoxW(IntPtr.Zero, text, caption, MB_YESNO | MB_ICONINFORMATION) == IDYES;

    // TASKDIALOGCONFIG and TASKDIALOG_BUTTON are declared with 1-byte packing in commctrl.h, so their
    // x64 sizes are 160 and 12, not the 176/16 natural alignment would give. Pack=1 reproduces that;
    // get it wrong and TaskDialogIndirect returns E_INVALIDARG and shows nothing, with no exception.

    internal enum UpdateAction { Update, ShowReleases, Cancel }

    private const uint TDF_ALLOW_DIALOG_CANCELLATION = 0x0008;
    private const uint TDF_SIZE_TO_CONTENT           = 0x01000000;
    private const uint TDCBF_CANCEL_BUTTON           = 0x0008;
    private const uint MB_TOPMOST                    = 0x00040000;

    // TD_INFORMATION_ICON = MAKEINTRESOURCEW(-3) = (WCHAR*)0xFFFD
    private static readonly IntPtr TD_INFORMATION_ICON = new(65533);

    // Field order matches commctrl.h exactly; Pack=1 gives the byte-packed layout the API expects.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TASKDIALOGCONFIG
    {
        public uint   cbSize;
        public IntPtr hwndParent;
        public IntPtr hInstance;
        public uint   dwFlags;
        public uint   dwCommonButtons;
        public IntPtr pszWindowTitle;
        public IntPtr hMainIcon;
        public IntPtr pszMainInstruction;
        public IntPtr pszContent;
        public uint   cButtons;
        public IntPtr pButtons;
        public int    nDefaultButton;
        public uint   cRadioButtons;
        public IntPtr pRadioButtons;
        public int    nDefaultRadioButton;
        public IntPtr pszVerificationText;
        public IntPtr pszExpandedInformation;
        public IntPtr pszExpandedControlText;
        public IntPtr pszCollapsedControlText;
        public IntPtr hFooterIcon;
        public IntPtr pszFooter;
        public IntPtr pfCallback;
        public IntPtr lpCallbackData;
        public uint   cxWidth;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TASKDIALOG_BUTTON
    {
        public int    nButtonID;
        public IntPtr pszButtonText;
    }

    [DllImport("comctl32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int TaskDialogIndirect(
        ref TASKDIALOGCONFIG pTaskConfig,
        out int              pnButton,
        IntPtr               pnRadioButton,
        IntPtr               pfVerificationFlagChecked);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>Captures the foreground HWND while still on the UI thread, before any async work.</summary>
    internal static IntPtr CaptureHwnd() => GetForegroundWindow();

    /// <summary>
    /// Shows the "update available" Task Dialog (Update / Releases page / Cancel) with an expandable
    /// release-notes section. Blocks until the user responds. Safe to call from any thread.
    /// </summary>
    /// <param name="canDownload">False when no direct download URL was found in the release assets,
    /// which drops the "Update" button.</param>
    internal static UpdateAction ShowUpdateDialog(
        string latestVersion, string runningVersion,
        string releaseNotes,  string appName,
        bool   canDownload,   IntPtr hwndParent = default)
    {
        if (hwndParent == IntPtr.Zero)
            hwndParent = GetForegroundWindow();

        var strings = new List<IntPtr>(12);
        IntPtr Str(string? s)
        {
            if (s is null) return IntPtr.Zero;
            var p = Marshal.StringToHGlobalUni(s);
            strings.Add(p);
            return p;
        }

        const int BtnUpdate   = 100;
        const int BtnReleases = 101;
        int   btnCount = canDownload ? 2 : 1;
        int   btnSize  = Marshal.SizeOf<TASKDIALOG_BUTTON>();
        var   pButtons = Marshal.AllocHGlobal(btnSize * btnCount);
        try
        {
            if (canDownload)
            {
                Marshal.StructureToPtr(
                    new TASKDIALOG_BUTTON { nButtonID = BtnUpdate,   pszButtonText = Str("Update") },
                    pButtons, false);
                Marshal.StructureToPtr(
                    new TASKDIALOG_BUTTON { nButtonID = BtnReleases, pszButtonText = Str("Releases page") },
                    IntPtr.Add(pButtons, btnSize), false);
            }
            else
            {
                Marshal.StructureToPtr(
                    new TASKDIALOG_BUTTON { nButtonID = BtnReleases, pszButtonText = Str("Releases page") },
                    pButtons, false);
            }

            var hasNotes = !string.IsNullOrWhiteSpace(releaseNotes);
            var config   = new TASKDIALOGCONFIG
            {
                cbSize                  = (uint)Marshal.SizeOf<TASKDIALOGCONFIG>(),
                hwndParent              = hwndParent,
                dwFlags                 = TDF_ALLOW_DIALOG_CANCELLATION | TDF_SIZE_TO_CONTENT,
                dwCommonButtons         = TDCBF_CANCEL_BUTTON,
                pszWindowTitle          = Str(appName),
                hMainIcon               = TD_INFORMATION_ICON,
                pszMainInstruction      = Str($"Version {latestVersion} is available"),
                pszContent              = Str($"You are running version {runningVersion}."),
                cButtons                = (uint)btnCount,
                pButtons                = pButtons,
                nDefaultButton          = canDownload ? BtnUpdate : BtnReleases,
                pszExpandedInformation  = Str(hasNotes ? releaseNotes : "No release notes provided."),
                pszCollapsedControlText = Str("Show release notes"),
                pszExpandedControlText  = Str("Hide release notes"),
            };

            int hr = TaskDialogIndirect(ref config, out int nButton, IntPtr.Zero, IntPtr.Zero);
            if (hr != 0)
            {
                // Degrade gracefully: plain MessageBox still lets the user reach the download.
                var pick = MessageBoxW(hwndParent,
                    $"Version {latestVersion} is available (you have {runningVersion}).\n\n" +
                    "Open the releases page to download it?",
                    appName, MB_YESNO | MB_ICONINFORMATION | MB_TOPMOST);
                return pick == IDYES ? UpdateAction.ShowReleases : UpdateAction.Cancel;
            }

            return nButton switch
            {
                BtnUpdate   => UpdateAction.Update,
                BtnReleases => UpdateAction.ShowReleases,
                _           => UpdateAction.Cancel,
            };
        }
        finally
        {
            foreach (var p in strings) Marshal.FreeHGlobal(p);
            Marshal.FreeHGlobal(pButtons);
        }
    }

}
