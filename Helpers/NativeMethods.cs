using System.Runtime.InteropServices;

namespace LenovoTray.Helpers;

/// <summary>Thin wrappers around Win32 APIs used across the app.</summary>
internal static class NativeMethods
{
    // SPI_GETWORKAREA: usable desktop area on the primary display, excluding the taskbar.
    private const uint SPI_GETWORKAREA = 0x0030;

    private const uint MONITOR_DEFAULTTONEAREST = 0x0002;
    private const int  MDT_EFFECTIVE_DPI        = 0;

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

    /// <summary>
    /// Work area (taskbar-excluded desktop bounds, in physical pixels) and DPI scale factor of the
    /// monitor currently under the mouse cursor — i.e. the screen whose tray the user just clicked.
    /// This positions the popup on the correct monitor and sizes it for that monitor's scaling,
    /// even in mixed-DPI multi-monitor setups.  Falls back to the primary monitor at 100 %.
    /// </summary>
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
    /// Usable desktop area on the primary monitor (total area minus the taskbar), in physical
    /// pixels.  Falls back to a sensible 1080p work area if the Win32 call fails.
    /// </summary>
    private static RECT GetPrimaryWorkArea()
    {
        if (SystemParametersInfo(SPI_GETWORKAREA, 0, out var rect, 0))
            return rect;

        // Fallback: assume a typical 1920×1040 work area (1080p minus a 40 px taskbar).
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
    }

    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    // ── Native Win32 dark-mode support ─────────────────────────────────────────
    // uxtheme.dll exposes these only by ordinal (no named exports).
    //   SetPreferredAppMode               = ordinal 135  (Win10 1903 / build 18362+)
    //   RefreshImmersiveColorPolicyState  = ordinal 104
    // Calling SetPreferredAppMode(AllowDark=1) at startup makes Windows render native Win32
    // elements (the H.NotifyIcon tray context menu, scrollbars) dark when the system theme is
    // dark — the same approach the sibling HyperVManagerTray app uses.
    [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = false)]
    private static extern int SetPreferredAppMode(int mode);   // 0=Default 1=AllowDark 2=ForceDark 3=ForceLight

    [DllImport("uxtheme.dll", EntryPoint = "#104", SetLastError = false)]
    private static extern void RefreshImmersiveColorPolicyState();

    /// <summary>
    /// Opts the process into Windows dark-mode rendering for native Win32 UI (the tray context
    /// menu). Call once, before any UI is created so the menu HWND inherits the setting. No-ops
    /// safely on older Windows builds where the ordinals do not exist.
    /// </summary>
    internal static void EnableDarkModeForNativeUi()
    {
        try
        {
            SetPreferredAppMode(1); // AllowDark — follows the system light/dark preference
            RefreshImmersiveColorPolicyState();
        }
        catch { /* ordinal absent on old builds — non-fatal */ }
    }

    // ── Message boxes (Win32) ──────────────────────────────────────────────────
    // Plain Win32 MessageBox — safe to call from any thread, works in this elevated
    // unpackaged app, and needs no WinUI XamlRoot. Used for About / update prompts.
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

    // ── Task Dialog — 3-button update prompt ────────────────────────────────────
    // TaskDialogIndirect supports fully custom button text and an expandable section,
    // making it ideal for the "update available" prompt with inline release notes.
    //
    // CRITICAL: TASKDIALOGCONFIG and TASKDIALOG_BUTTON are declared with 1-byte packing
    // in commctrl.h (they sit inside a #include <pshpack1.h> … <poppack.h> block), so the
    // x64 sizes are 160 and 12 — NOT the 176/16 you'd get from natural 8-byte alignment.
    // Pack=1 reproduces that. If the size/offsets are wrong, TaskDialogIndirect rejects the
    // call with E_INVALIDARG and silently shows nothing (no exception), so "Check for
    // updates" appears to do nothing.

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

    /// <summary>x64 marshalled size of TASKDIALOGCONFIG (must be 160).</summary>
    internal static int TaskDialogConfigSize => Marshal.SizeOf<TASKDIALOGCONFIG>();
    /// <summary>x64 marshalled size of TASKDIALOG_BUTTON (must be 12).</summary>
    internal static int TaskDialogButtonSize => Marshal.SizeOf<TASKDIALOG_BUTTON>();

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
    /// Shows the "update available" Task Dialog with up to three buttons
    /// (Update / Releases page / Cancel) and an expandable release-notes section.
    /// Blocks until the user responds. Safe to call from any thread.
    /// </summary>
    /// <param name="canDownload">
    /// When <c>true</c> an "Update" button is shown; when <c>false</c> only
    /// "Releases page" is shown (no direct download URL found in release assets).
    /// </param>
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

    // ── Common file dialogs (comdlg32) ─────────────────────────────────────────
    // The app is requireAdministrator (elevated). The WinRT FileOpenPicker/FileSavePicker
    // are unreliable in elevated processes, so settings Export/Import use the classic Win32
    // common dialogs, which work correctly regardless of integrity level.

    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_NOCHANGEDIR     = 0x00000008;
    private const int OFN_PATHMUSTEXIST   = 0x00000800;
    private const int OFN_FILEMUSTEXIST   = 0x00001000;
    private const int OFN_EXPLORER        = 0x00080000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int    lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrFilter;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrCustomFilter;
        public int    nMaxCustFilter;
        public int    nFilterIndex;
        public IntPtr lpstrFile;          // caller-allocated in/out buffer
        public int    nMaxFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrFileTitle;
        public int    nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrTitle;
        public int    Flags;
        public short  nFileOffset;
        public short  nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTemplateName;
        public IntPtr pvReserved;
        public int    dwReserved;
        public int    FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAME ofn);

    /// <summary>Shows a Save-As dialog; returns the chosen path, or null if cancelled.</summary>
    internal static string? ShowSaveFileDialog(IntPtr owner, string title, string defaultFileName,
        string defExt, string filter)
        => ShowFileDialog(owner, title, defaultFileName, defExt, filter, save: true);

    /// <summary>Shows an Open dialog; returns the chosen path, or null if cancelled.</summary>
    internal static string? ShowOpenFileDialog(IntPtr owner, string title, string defExt, string filter)
        => ShowFileDialog(owner, title, "", defExt, filter, save: false);

    // filter uses '|' as the section separator (e.g. "JSON (*.json)|*.json|All files|*.*"),
    // translated to the API's null-separated, double-null-terminated form internally.
    private static string? ShowFileDialog(IntPtr owner, string title, string defaultFileName,
        string defExt, string filter, bool save)
    {
        const int bufChars = 1024;
        IntPtr buffer = Marshal.AllocHGlobal(bufChars * sizeof(char));
        try
        {
            // Zero the buffer, then write the (optional) default file name, null-terminated.
            var bytes = new byte[bufChars * sizeof(char)];
            System.Text.Encoding.Unicode.GetBytes(defaultFileName ?? "").CopyTo(bytes, 0);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);

            var ofn = new OPENFILENAME
            {
                lStructSize  = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner    = owner,
                lpstrFilter  = filter.Replace('|', '\0') + "\0",
                lpstrFile    = buffer,
                nMaxFile     = bufChars,
                lpstrTitle   = title,
                lpstrDefExt  = defExt,
                nFilterIndex = 1,
                Flags        = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_PATHMUSTEXIST |
                               (save ? OFN_OVERWRITEPROMPT : OFN_FILEMUSTEXIST),
            };

            bool ok = save ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
            return ok ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
