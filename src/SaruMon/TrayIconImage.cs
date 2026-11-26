using System.Runtime.InteropServices;

namespace SaruMon;

internal static class TrayIconImage
{
    internal static Icon FromBitmap(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}
