using System.Runtime.InteropServices;
using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Asks for a program file through the Win32 common dialog.
/// </summary>
/// <remarks>
/// The Win32 dialog rather than WinRT's picker: the picker is brokered by a process running as the
/// signed-in person, and this application is <c>requireAdministrator</c>, so the broker refuses it.
/// The common dialog runs inside the calling process and has no such split.
/// </remarks>
internal static class ProgramFileDialog
{
    private const int MaxPath = 1024;

    // OPENFILENAME flags.
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_NOCHANGEDIR   = 0x00000008;
    private const int OFN_EXPLORER      = 0x00080000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int structSize;
        public IntPtr owner;
        public IntPtr instance;
        public string? filter;
        public string? customFilter;
        public int maxCustomFilter;
        public int filterIndex;
        public string? file;
        public int maxFile;
        public string? fileTitle;
        public int maxFileTitle;
        public string? initialDir;
        public string? title;
        public int flags;
        public short fileOffset;
        public short fileExtension;
        public string? defExt;
        public IntPtr custData;
        public IntPtr hook;
        public string? templateName;
        public IntPtr reservedPointer;
        public int reservedInt;
        public int flagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAME dialog);

    /// <summary>The program chosen, or null where the dialog was dismissed or could not be
    /// shown.</summary>
    /// <param name="owner">The window the dialog belongs to, so it cannot be lost behind it.</param>
    internal static string? Choose(IntPtr owner, string title)
    {
        try
        {
            // The filter is a run of null-terminated pairs closed by an empty one, which is why it
            // cannot be written as an ordinary string.
            string filter = "Programs\0*.exe\0\0";

            var dialog = new OPENFILENAME
            {
                structSize  = Marshal.SizeOf<OPENFILENAME>(),
                owner       = owner,
                filter      = filter,
                filterIndex = 1,
                file        = new string('\0', MaxPath),
                maxFile     = MaxPath,
                title       = title,
                defExt      = "exe",
                flags       = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER,
            };

            if (!GetOpenFileNameW(ref dialog)) return null;

            string chosen = dialog.file?.TrimEnd('\0') ?? "";
            return chosen.Length > 0 ? chosen : null;
        }
        catch (Exception ex)
        {
            AppLog.Error("ProgramFileDialog.Choose", ex);
            return null;
        }
    }
}
