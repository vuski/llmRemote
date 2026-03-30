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
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

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

    // Previous frame data for diff detection
    private byte[]? _previousPixels;
    private int _previousWidth;
    private int _previousHeight;

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

    /// <summary>
    /// Capture window and return WebP-encoded frame.
    /// Returns null if nothing changed since last capture.
    /// Returns a FrameData with isDiff=true if only a region changed.
    /// </summary>
    public FrameData? CaptureWindow(IntPtr hWnd, int quality = 85)
    {
        if (!GetClientRect(hWnd, out var clientRect))
            return null;

        int w = clientRect.Width, h = clientRect.Height;
        if (w <= 0 || h <= 0)
            return null;

        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        bool success = PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT);
        graphics.ReleaseHdc(hdc);

        if (!success)
            return null;

        // Get raw pixel data
        var bmpData = bitmap.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        var currentPixels = new byte[bmpData.Stride * h];
        Marshal.Copy(bmpData.Scan0, currentPixels, 0, currentPixels.Length);
        int stride = bmpData.Stride;

        // Check for changes
        if (_previousPixels != null && _previousWidth == w && _previousHeight == h)
        {
            var diffRect = FindDiffRect(currentPixels, _previousPixels, w, h, stride);

            if (diffRect == null)
            {
                // No change at all
                bitmap.UnlockBits(bmpData);
                return null;
            }

            // If diff region is small enough, send only that region
            var (dx, dy, dw, dh) = diffRect.Value;
            float diffArea = (float)(dw * dh) / (w * h);

            if (diffArea < 0.7f && dw > 0 && dh > 0)
            {
                // Encode just the diff region
                var diffBytes = EncodeRegionWebP(bmpData.Scan0, w, h, stride, dx, dy, dw, dh, quality);
                bitmap.UnlockBits(bmpData);
                _previousPixels = currentPixels;

                if (diffBytes != null)
                {
                    return new FrameData
                    {
                        Data = diffBytes,
                        IsDiff = true,
                        X = dx, Y = dy, Width = dw, Height = dh,
                        FullWidth = w, FullHeight = h
                    };
                }
            }
        }

        // Full frame encode
        var fullBytes = EncodeWebP(bmpData.Scan0, w, h, stride, quality);
        bitmap.UnlockBits(bmpData);

        _previousPixels = currentPixels;
        _previousWidth = w;
        _previousHeight = h;

        if (fullBytes == null) return null;

        return new FrameData
        {
            Data = fullBytes,
            IsDiff = false,
            X = 0, Y = 0, Width = w, Height = h,
            FullWidth = w, FullHeight = h
        };
    }

    /// <summary>
    /// Find the bounding rectangle of changed pixels.
    /// Returns null if no change. Compares in 4x4 blocks for speed.
    /// </summary>
    private (int x, int y, int w, int h)? FindDiffRect(byte[] current, byte[] previous,
        int width, int height, int stride)
    {
        int minX = width, minY = height, maxX = 0, maxY = 0;
        bool found = false;

        // Compare in blocks of 4 pixels for speed
        const int blockSize = 4;
        for (int y = 0; y < height; y += blockSize)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < width; x += blockSize)
            {
                int offset = rowOffset + x * 4;
                bool blockDiff = false;

                // Quick check: compare 16 bytes at once (one pixel row of the block)
                for (int i = 0; i < Math.Min(blockSize, height - y) * stride && !blockDiff; i += stride)
                {
                    for (int j = 0; j < Math.Min(blockSize * 4, (width - x) * 4); j += 4)
                    {
                        int idx = offset + i + j - rowOffset + rowOffset;
                        if (idx + 3 >= current.Length) break;
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
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x + blockSize > maxX) maxX = Math.Min(x + blockSize, width);
                    if (y + blockSize > maxY) maxY = Math.Min(y + blockSize, height);
                }
            }
        }

        if (!found) return null;

        // Add some padding for safety
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
        // Create a sub-bitmap for the diff region
        using var regionBitmap = new Bitmap(rw, rh, PixelFormat.Format32bppArgb);
        var regionData = regionBitmap.LockBits(new Rectangle(0, 0, rw, rh),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        // Copy region pixels
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
        // Reset diff state on resize
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
