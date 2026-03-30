using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace LlmRemote.Services;

public class WindowService
{
    #region Win32 P/Invoke

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute,
        out bool pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int DWMWA_CLOAKED = 14;

    #endregion

    #region WebP P/Invoke

    [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl)]
    private static extern int WebPEncodeBGRA(IntPtr bgra, int width, int height, int stride,
        float quality_factor, out IntPtr output);

    [DllImport("libwebp", CallingConvention = CallingConvention.Cdecl)]
    private static extern void WebPFree(IntPtr ptr);

    #endregion

    private byte[]? _previousPixels;
    private int _previousWidth;
    private int _previousHeight;
    private readonly int _quality;

    public WindowService(int quality = 85)
    {
        _quality = Math.Clamp(quality, 1, 100);
        Log.Write($"[Capture] WebP quality: {_quality}");
    }

    public List<WindowInfo> GetWindows()
    {
        var windows = new List<WindowInfo>();
        var shellWindow = GetShellWindow();

        EnumWindows((hWnd, _) =>
        {
            if (hWnd == shellWindow) return true;
            if (!IsWindowVisible(hWnd)) return true;

            var titleLength = GetWindowTextLength(hWnd);
            if (titleLength == 0) return true;

            DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out bool cloaked, Marshal.SizeOf<bool>());
            if (cloaked) return true;

            var sb = new StringBuilder(titleLength + 1);
            GetWindowText(hWnd, sb, sb.Capacity);

            GetWindowRect(hWnd, out var rect);
            if (rect.Width <= 0 || rect.Height <= 0) return true;

            windows.Add(new WindowInfo
            {
                Hwnd = hWnd.ToInt64(),
                Title = sb.ToString(),
                Width = rect.Width,
                Height = rect.Height
            });

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public FrameData? CaptureWindow(IntPtr hWnd)
    {
        if (!GetClientRect(hWnd, out var clientRect))
            return null;

        int w = clientRect.Width, h = clientRect.Height;
        if (w <= 0 || h <= 0) return null;

        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        bool success = PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT);
        graphics.ReleaseHdc(hdc);
        if (!success) return null;

        var bmpData = bitmap.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        var currentPixels = new byte[bmpData.Stride * h];
        Marshal.Copy(bmpData.Scan0, currentPixels, 0, currentPixels.Length);
        int stride = bmpData.Stride;

        // Diff detection
        if (_previousPixels != null && _previousWidth == w && _previousHeight == h)
        {
            var diffRect = FindDiffRect(currentPixels, _previousPixels, w, h, stride);
            if (diffRect == null)
            {
                bitmap.UnlockBits(bmpData);
                return null; // No change
            }

            var (dx, dy, dw, dh) = diffRect.Value;
            float diffArea = (float)(dw * dh) / (w * h);

            if (diffArea < 0.7f && dw > 0 && dh > 0)
            {
                var diffBytes = EncodeRegionWebP(bmpData.Scan0, w, h, stride, dx, dy, dw, dh, _quality);
                bitmap.UnlockBits(bmpData);
                _previousPixels = currentPixels;
                if (diffBytes != null)
                {
                    return new FrameData
                    {
                        Data = diffBytes, IsDiff = true,
                        X = dx, Y = dy, Width = dw, Height = dh,
                        FullWidth = w, FullHeight = h
                    };
                }
            }
        }

        // Full frame
        var fullBytes = EncodeWebP(bmpData.Scan0, w, h, stride, _quality);
        bitmap.UnlockBits(bmpData);
        _previousPixels = currentPixels;
        _previousWidth = w;
        _previousHeight = h;
        if (fullBytes == null) return null;

        return new FrameData
        {
            Data = fullBytes, IsDiff = false,
            X = 0, Y = 0, Width = w, Height = h,
            FullWidth = w, FullHeight = h
        };
    }

    private (int x, int y, int w, int h)? FindDiffRect(byte[] current, byte[] previous,
        int width, int height, int stride)
    {
        int minX = width, minY = height, maxX = 0, maxY = 0;
        bool found = false;
        int changedBlocks = 0;
        const int blockSize = 8;

        for (int y = 0; y < height; y += blockSize)
        {
            for (int x = 0; x < width; x += blockSize)
            {
                int offset = y * stride + x * 4;
                bool blockDiff = false;

                for (int by = 0; by < blockSize && y + by < height && !blockDiff; by++)
                {
                    int rowOff = offset + by * stride;
                    for (int bx = 0; bx < blockSize && x + bx < width; bx++)
                    {
                        int idx = rowOff + bx * 4;
                        if (idx + 2 >= current.Length) break;
                        if (current[idx] != previous[idx] ||
                            current[idx + 1] != previous[idx + 1] ||
                            current[idx + 2] != previous[idx + 2])
                        {
                            blockDiff = true;
                            break;
                        }
                    }
                }

                if (blockDiff)
                {
                    found = true;
                    changedBlocks++;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x + blockSize > maxX) maxX = Math.Min(x + blockSize, width);
                    if (y + blockSize > maxY) maxY = Math.Min(y + blockSize, height);
                }
            }
        }

        if (!found || changedBlocks <= 2) return null; // Ignore tiny changes (cursor blink etc.)
        minX = Math.Max(0, minX - 2);
        minY = Math.Max(0, minY - 2);
        maxX = Math.Min(width, maxX + 2);
        maxY = Math.Min(height, maxY + 2);
        return (minX, minY, maxX - minX, maxY - minY);
    }

    private byte[]? EncodeWebP(IntPtr pixels, int width, int height, int stride, int quality)
    {
        int size = WebPEncodeBGRA(pixels, width, height, stride, quality, out var output);
        if (size <= 0) return null;
        var result = new byte[size];
        Marshal.Copy(output, result, 0, size);
        WebPFree(output);
        return result;
    }

    private byte[]? EncodeRegionWebP(IntPtr fullPixels, int fullWidth, int fullHeight,
        int stride, int rx, int ry, int rw, int rh, int quality)
    {
        using var regionBitmap = new Bitmap(rw, rh, PixelFormat.Format32bppArgb);
        var regionData = regionBitmap.LockBits(new Rectangle(0, 0, rw, rh),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* src = (byte*)fullPixels;
            byte* dst = (byte*)regionData.Scan0;
            for (int y = 0; y < rh; y++)
            {
                int srcOffset = (ry + y) * stride + rx * 4;
                int dstOffset = y * regionData.Stride;
                Buffer.MemoryCopy(src + srcOffset, dst + dstOffset, regionData.Stride, rw * 4);
            }
        }
        int size = WebPEncodeBGRA(regionData.Scan0, rw, rh, regionData.Stride, quality, out var output);
        regionBitmap.UnlockBits(regionData);
        if (size <= 0) return null;
        var result = new byte[size];
        Marshal.Copy(output, result, 0, size);
        WebPFree(output);
        return result;
    }

    public void ResizeWindow(IntPtr hWnd, int width, int height)
    {
        SetWindowPos(hWnd, IntPtr.Zero, 0, 0, width, height, SWP_NOMOVE | SWP_NOZORDER);
        _previousPixels = null;
    }

    public RECT GetWindowSize(IntPtr hWnd)
    {
        GetWindowRect(hWnd, out var rect);
        return rect;
    }
}

public class FrameData
{
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public bool IsDiff { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int FullWidth { get; set; }
    public int FullHeight { get; set; }
}

public class WindowInfo
{
    public long Hwnd { get; set; }
    public string Title { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}
