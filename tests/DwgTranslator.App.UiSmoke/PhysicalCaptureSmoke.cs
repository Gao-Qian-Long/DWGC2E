using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DwgTranslator.App.Views;

namespace UiSmoke;

public sealed partial class SmokeApp
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CaptureRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out CaptureRect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr target, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    // Unlike VisualBrush snapshots, this records the actual physical monitor pixels,
    // including DPI scaling, chrome and any real clipping at the window boundary.
    private static void CapturePhysicalWindow(MainWindow window, string path)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (!GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("GetWindowRect failed");
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 1 || height < 1) throw new InvalidOperationException("Empty native window rect");
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            // BitBlt of the desktop silently captures unrelated foreground windows when
            // another app covers the smoke HWND. Print the HWND itself at its native pixel
            // dimensions so a background browser cannot contaminate layout evidence.
            if (!PrintWindow(hwnd, memory, 0x00000002))
                throw new InvalidOperationException("Native window PrintWindow failed");
            var frame = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var stream = File.Create(path);
            encoder.Save(stream);
            Console.WriteLine($"PHYSICAL_CAPTURE {Path.GetFileName(path)} {width}x{height} screen=({rect.Left},{rect.Top})");
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
