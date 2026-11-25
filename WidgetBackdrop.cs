using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace BatteryWidget;

internal static class WidgetBackdrop
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr bitmap);

    internal static BitmapSource? Capture(IntPtr window, Int32Rect region)
    {
        if (window == IntPtr.Zero || region.Width is < 1 or > 4096 || region.Height is < 1 or > 4096
            || !SetWindowDisplayAffinity(window, 0x11))
            return null;

        try
        {
            _ = DwmFlush();
            using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(region.X, region.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);

            IntPtr handle = bitmap.GetHbitmap();
            try
            {
                var image = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally
            {
                _ = DeleteObject(handle);
            }
        }
        catch (Win32Exception)
        {
            return null;
        }
        finally
        {
            _ = SetWindowDisplayAffinity(window, 0);
        }
    }
}
