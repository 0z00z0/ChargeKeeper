using System.Runtime.InteropServices;
using Windows.Graphics;
using ZeroZero.Win32;

namespace ChargeKeeper.Helpers;

/// <summary>Thin wrappers around Win32 APIs used across the app.</summary>
internal static class NativeMethods
{
    private const uint MONITOR_DEFAULTTONEAREST = 0x0002;

    // ES_CONTINUOUS makes the request stick until cleared rather than resetting one idle timer. The
    // state is PER-THREAD: set and clear must happen on the same long-lived thread.
    internal const uint ES_CONTINUOUS       = 0x80000000;
    internal const uint ES_SYSTEM_REQUIRED  = 0x00000001;
    internal const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    internal static extern uint SetThreadExecutionState(uint esFlags);

    // The counter that STOPS while the machine is suspended. The ordinary tick count and the wall
    // clock both keep running across a sleep, so neither tells a held-awake span from a slept one.
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    /// <summary>
    /// How long this machine has been awake since it started, excluding every span it spent
    /// suspended. The unit is 100 ns, which is a <see cref="TimeSpan"/> tick. Null when the query
    /// fails, which callers must not read as zero.
    /// </summary>
    internal static TimeSpan? UnbiasedAwakeTime()
    {
        try
        {
            return QueryUnbiasedInterruptTime(out ulong ticks) ? TimeSpan.FromTicks((long)ticks) : null;
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>
    /// How long since keyboard or mouse input last reached THIS session. Null when the query fails,
    /// which callers must not read as zero.
    /// </summary>
    /// <remarks>
    /// The tick is 32-bit and wraps at about 49.7 days of uptime, so the subtraction is unsigned and
    /// wraps with it. The reading sees only the calling session, so a small figure is proof that
    /// somebody was at the machine and a large one is weak evidence of the opposite: input on the
    /// secure desktop, in another session, or over some remote paths never reaches it. Locking the
    /// workstation stops the tick advancing, so this must be read before any lock.
    /// </remarks>
    internal static TimeSpan? SinceLastInput()
    {
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return null;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
        }
        catch { return null; }
    }

    // SetThreadExecutionState cannot hold off a lid-close sleep: lid close is a power-policy action,
    // not an idle timeout. Delaying it means overriding the user's LIDACTION to "do nothing" and
    // putting it back afterwards.
    private static readonly Guid GUID_SUB_BUTTONS = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static readonly Guid GUID_LIDACTION   = new("5ca83367-6e45-459f-a27b-476b1d01c936");
    private static readonly Guid GUID_LIDSWITCH_STATE_CHANGE = new("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");

    // Windows' own idle sleep, the rule a released execution-state hold hands back to. Same shape as
    // the lid action above — per-scheme, one AC and one DC value — with a different subgroup and
    // setting. The unit is seconds and zero means never.
    private static readonly Guid GUID_SUB_SLEEP   = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid GUID_STANDBYIDLE = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

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

    // Two more BOOLEANs in the same struct. SystemS3 is the sixth field, AoAc the twenty-first —
    // AoAc set is what makes the platform report "Standby (S0 Low Power Idle)". Every field up to
    // both is a single byte, so the byte index is the field index.
    private const int SystemS3Offset = 5;
    private const int AoAcOffset     = 20;

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

    /// <summary>
    /// Whether this machine does Modern Standby, and whether it offers traditional S3, from the same
    /// power capabilities as <see cref="LidPresent"/>. Null when the query fails.
    /// </summary>
    internal static (bool ModernStandby, bool SupportsS3)? StandbyFlags()
    {
        var buffer = new byte[256];
        try
        {
            return GetPwrCapabilities(buffer)
                ? (buffer[AoAcOffset] != 0, buffer[SystemS3Offset] != 0)
                : null;
        }
        catch { return null; }
    }

    // Rooted for each subscription's lifetime, keyed by its registration handle: the OS keeps a RAW
    // function pointer to the delegate, which the GC cannot see. Letting one be collected turns the
    // next lid event on that registration into a hard crash. Concurrent because Lid delay and a
    // script subscription each hold their own registration independently.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, DeviceNotifyCallback> _lidCallbacks = new();

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
    /// The active scheme's AC and DC idle sleep delays, in seconds, where zero means Windows never
    /// sleeps this machine on idle. Read only, so the scheme is not returned with them. Null when
    /// the query fails, and null must never be read as zero: zero is a promise that nothing sleeps
    /// the machine on its own, which a failed read is no evidence of.
    /// </summary>
    internal static (uint AcSeconds, uint DcSeconds)? ReadSleepDelay() =>
        WithActiveScheme<(uint, uint)?>((scheme, _) =>
        {
            var s = scheme; var sub = GUID_SUB_SLEEP; var setting = GUID_STANDBYIDLE;
            if (PowerReadACValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, out uint ac) != 0) return null;
            if (PowerReadDCValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, out uint dc) != 0) return null;
            return (ac, dc);
        }, null);

    /// <summary>
    /// Sets <paramref name="scheme"/>'s AC and DC lid-close action indices. Returns false if any step
    /// failed, in which case the caller must assume the scheme is in an unknown state and re-read it.
    /// Targets an explicit scheme, so a power-plan switch between capture and restore cannot write
    /// one plan's saved values into another. Writing the value is not enough — the scheme must be
    /// re-activated for the change to reach the running system, hence the closing PowerSetActiveScheme.
    /// </summary>
    internal static bool WriteLidCloseAction(Guid scheme, uint ac, uint dc) =>
        WriteAndActivate(scheme, GUID_SUB_BUTTONS, GUID_LIDACTION, ac, dc);

    /// <summary>
    /// The active scheme's GUID together with its battery (DC) idle sleep delay in seconds, zero
    /// meaning never. Null when the query fails, and null must never be read as zero: the caller
    /// persists this to restore later, and a bogus zero would leave the battery never sleeping.
    /// </summary>
    internal static (Guid Scheme, uint DcSeconds)? ReadActiveBatterySleepDelay() =>
        WithActiveScheme<(Guid, uint)?>((scheme, _) =>
        {
            var s = scheme; var sub = GUID_SUB_SLEEP; var setting = GUID_STANDBYIDLE;
            if (PowerReadDCValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, out uint dc) != 0) return null;
            return (scheme, dc);
        }, null);

    /// <summary>Sets <paramref name="scheme"/>'s battery (DC) idle sleep delay alone, leaving the
    /// mains value untouched. Same contract as <see cref="WriteLidCloseAction"/>.</summary>
    internal static bool WriteBatterySleepDelay(Guid scheme, uint dcSeconds) =>
        WriteAndActivate(scheme, GUID_SUB_SLEEP, GUID_STANDBYIDLE, ac: null, dcSeconds);

    /// <summary>Writes one setting's AC and/or DC index into an explicit scheme, then re-activates the
    /// active scheme, which is what makes a written value reach the running system.</summary>
    private static bool WriteAndActivate(Guid scheme, Guid subGroup, Guid powerSetting, uint? ac, uint? dc) =>
        WithActiveScheme((_, activeRaw) =>
        {
            var s = scheme; var sub = subGroup; var setting = powerSetting;
            if (ac is { } acValue && PowerWriteACValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, acValue) != 0) return false;
            if (dc is { } dcValue && PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref sub, ref setting, dcValue) != 0) return false;
            // Marked before the call rather than after it: a re-delivery this write provokes can
            // reach the lid callback while PowerSetActiveScheme is still running.
            LastSchemeActivatedAt = DateTimeOffset.Now;
            return PowerSetActiveScheme(IntPtr.Zero, activeRaw) == 0;
        }, false);

    /// <summary>When this process last re-activated the power scheme, or null when it never has.
    /// Every settings reload reaches that write while a lid subscription is live, so a lid
    /// notification landing beside one may be the write's own echo rather than a lid moving.</summary>
    internal static DateTimeOffset? LastSchemeActivatedAt { get; private set; }

    /// <summary>
    /// Subscribes to lid open/close, invoking <paramref name="onLidState"/> with the byte Windows
    /// delivered — 0 closed, 1 open. The raw value is passed rather than a reading of it: what
    /// arrived is the observation, and a trail holding only the conclusion cannot be used to tell a
    /// real close from a false one. Returns a registration handle for
    /// <see cref="UnregisterLidNotification"/>, or IntPtr.Zero if the subscription failed.
    /// <para>Windows invokes the callback once immediately with the current lid state, before any
    /// real transition — the caller must treat that first reading as a seed, not as a lid close.</para>
    /// <para>Windows accepts more than one registration per process, each with its own handle and its
    /// own replay of the current state — measured, not merely documented. Callers needing different
    /// lifetimes (Lid delay, and scripts bound to the lid) register independently rather than sharing
    /// one subscription.</para>
    /// </summary>
    internal static IntPtr RegisterLidNotification(Action<byte> onLidState)
    {
        DeviceNotifyCallback callback = (_, type, setting) =>
        {
            if (type == PBT_POWERSETTINGCHANGE && setting != IntPtr.Zero)
            {
                var s = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(setting);
                if (s.PowerSetting == GUID_LIDSWITCH_STATE_CHANGE && s.DataLength >= 1)
                    onLidState(s.Data);   // 0 = closed, 1 = open
            }
            return 0;   // ERROR_SUCCESS
        };

        try
        {
            var recipient = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
            {
                Callback = Marshal.GetFunctionPointerForDelegate(callback),
                Context  = IntPtr.Zero,
            };
            var guid = GUID_LIDSWITCH_STATE_CHANGE;
            if (PowerSettingRegisterNotification(ref guid, DEVICE_NOTIFY_CALLBACK, ref recipient, out var handle) == 0)
            {
                _lidCallbacks[handle] = callback;   // rooted before the OS can deliver on it
                return handle;
            }
        }
        catch { /* absent on older builds — the caller degrades to "no lid events" */ }

        return IntPtr.Zero;
    }

    /// <summary>Ends a <see cref="RegisterLidNotification"/> subscription. Safe on IntPtr.Zero.</summary>
    internal static void UnregisterLidNotification(IntPtr registration)
    {
        if (registration == IntPtr.Zero) return;
        try { PowerSettingUnregisterNotification(registration); }
        catch { /* nothing useful to do while tearing down */ }
        _lidCallbacks.TryRemove(registration, out _);
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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>The system double-click interval. Read every time rather than cached — it is a user
    /// setting that changes without notifying us.</summary>
    internal static TimeSpan DoubleClickTime => TimeSpan.FromMilliseconds(GetDoubleClickTime());

    /// <summary>
    /// Opening rect (physical px) for a window of <paramref name="dipWidth"/> × <paramref name="dipHeight"/>
    /// DIPs, centred on the monitor under the cursor and capped to its work area. Takes DIPs rather
    /// than a caller-computed pixel size because size and position must come from the same monitor's
    /// metrics, or a mixed-DPI setup mis-sizes the window.
    /// </summary>
    internal static RectInt32 CentreRectOnCursorMonitor(int dipWidth, int dipHeight)
    {
        var (work, scale) = MonitorMetrics.ForCursor();
        return CentreInWorkArea(work,
                                Math.Min((int)Math.Round(dipWidth  * scale), work.Width),
                                Math.Min((int)Math.Round(dipHeight * scale), work.Height));
    }

    /// <summary>
    /// Centres an already-sized <paramref name="w"/> × <paramref name="h"/> rect (physical px) inside
    /// <paramref name="work"/>. Deliberately not clamped: a rect larger than the work area centres
    /// with symmetric overhang rather than being pinned to the top-left corner, which is what the
    /// callers that intentionally oversize want.
    /// </summary>
    internal static RectInt32 CentreInWorkArea(NativeRect work, int w, int h)
        => new(work.Left + (work.Width  - w) / 2,
               work.Top  + (work.Height - h) / 2,
               w, h);

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
    /// monitor query fails. The shared Win32 layer answers for a point, not for the monitor nearest a
    /// rectangle, so this stays here.</summary>
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>Captures the foreground HWND while still on the UI thread, before any async work.</summary>
    internal static IntPtr CaptureHwnd() => GetForegroundWindow();

    // ── This process's own resource use ─────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint  cb;
        public uint  PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;      // the EX member; absent from the plain structure
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    // A pseudo-handle for the calling process. Constant, never closed.
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetProcessMemoryInfo(
        IntPtr process, out PROCESS_MEMORY_COUNTERS_EX counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetProcessHandleCount(IntPtr process, out uint count);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

    /// <summary>What this process is using, as Windows accounts it. Byte figures are absolute;
    /// the two transfer totals are cumulative since the process started.</summary>
    internal readonly record struct ProcessResourceCounters(
        long WorkingSetBytes, long PrivateBytes, int Handles, long ReadBytes, long WriteBytes);

    /// <summary>
    /// Memory, handle count and cumulative I/O for this process. Null when any of the three queries
    /// fails, which callers must not read as zero.
    /// </summary>
    /// <remarks>
    /// Three direct queries against the process pseudo-handle, about 1.5 µs together and allocating
    /// nothing. The <see cref="System.Diagnostics.Process"/> route reaches the same figures only
    /// through a snapshot of every process on the machine, which costs milliseconds and scales with
    /// how many processes are running. Thread count is not here: every route to it is that same
    /// machine-wide enumeration.
    /// </remarks>
    internal static ProcessResourceCounters? CurrentProcessResources()
    {
        try
        {
            var self = GetCurrentProcess();

            // The size decides which structure Windows fills: the EX form, and so PrivateUsage.
            uint size = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>();
            if (!GetProcessMemoryInfo(self, out var memory, size)) return null;
            if (!GetProcessHandleCount(self, out uint handles)) return null;
            if (!GetProcessIoCounters(self, out var io)) return null;

            return new ProcessResourceCounters(
                (long)memory.WorkingSetSize,
                (long)memory.PrivateUsage,
                (int)handles,
                (long)io.ReadTransferCount,
                (long)io.WriteTransferCount);
        }
        catch { return null; }
    }

}
