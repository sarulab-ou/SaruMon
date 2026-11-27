using System.Runtime.InteropServices;

namespace SaruMon;

internal static class TrayIconImage
{
    internal static readonly Color FrameColor = Color.FromArgb(181, 181, 186);

    internal static int FrameWidth(int size) => Math.Max(1, (size + 8) / 16);

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
