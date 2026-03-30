using System.Runtime.InteropServices;

namespace LlmRemote.Services;

public class InputService
{
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_CHAR = 0x0102;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_MOUSEMOVE = 0x0200;

    public void SendKeyDown(IntPtr hWnd, int vkCode)
    {
        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)vkCode, IntPtr.Zero);
    }

    public void SendKeyUp(IntPtr hWnd, int vkCode)
    {
        PostMessage(hWnd, WM_KEYUP, (IntPtr)vkCode, IntPtr.Zero);
    }

    public void SendChar(IntPtr hWnd, char ch)
    {
        // Use SendInput with KEYEVENTF_UNICODE — works for all apps including Electron
        SetForegroundWindow(hWnd);

        var inputs = new INPUT[2];

        // Key down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = 0;
        inputs[0].u.ki.wScan = (ushort)ch;
        inputs[0].u.ki.dwFlags = KEYEVENTF_UNICODE;

        // Key up
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = 0;
        inputs[1].u.ki.wScan = (ushort)ch;
        inputs[1].u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;

        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    public void SendMouseClick(IntPtr hWnd, int x, int y, bool rightButton = false)
    {
        var lParam = MakeLParam(x, y);
        var downMsg = rightButton ? WM_RBUTTONDOWN : WM_LBUTTONDOWN;
        var upMsg = rightButton ? WM_RBUTTONUP : WM_LBUTTONUP;
        PostMessage(hWnd, downMsg, IntPtr.Zero, lParam);
        PostMessage(hWnd, upMsg, IntPtr.Zero, lParam);
    }

    public void SendMouseMove(IntPtr hWnd, int x, int y)
    {
        var lParam = MakeLParam(x, y);
        PostMessage(hWnd, WM_MOUSEMOVE, IntPtr.Zero, lParam);
    }

    public void SendScroll(IntPtr hWnd, int x, int y, int delta)
    {
        // WM_MOUSEWHEEL needs screen coordinates in lParam
        var pt = new POINT { X = x, Y = y };
        ClientToScreen(hWnd, ref pt);
        var wParam = (IntPtr)(delta << 16);
        var lParam = MakeLParam(pt.X, pt.Y);
        SendMessage(hWnd, WM_MOUSEWHEEL, wParam, lParam);
    }

    public void PasteText(IntPtr hWnd, string text, bool sendEnter = false)
    {
        SetForegroundWindow(hWnd);
        Thread.Sleep(50);

        var thread = new Thread(() =>
        {
            System.Windows.Forms.Clipboard.SetText(text);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Thread.Sleep(30);

        // Ctrl+V, then optionally Enter
        int count = sendEnter ? 6 : 4;
        var inputs = new INPUT[count];

        // Ctrl down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = 0x11;

        // V down
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = 0x56;

        // V up
        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].u.ki.wVk = 0x56;
        inputs[2].u.ki.dwFlags = KEYEVENTF_KEYUP;

        // Ctrl up
        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].u.ki.wVk = 0x11;
        inputs[3].u.ki.dwFlags = KEYEVENTF_KEYUP;

        if (sendEnter)
        {
            // Enter down
            inputs[4].type = INPUT_KEYBOARD;
            inputs[4].u.ki.wVk = 0x0D; // VK_RETURN

            // Enter up
            inputs[5].type = INPUT_KEYBOARD;
            inputs[5].u.ki.wVk = 0x0D;
            inputs[5].u.ki.dwFlags = KEYEVENTF_KEYUP;
        }

        SendInput((uint)count, inputs, Marshal.SizeOf<INPUT>());
    }

    public void FocusWindow(IntPtr hWnd)
    {
        SetForegroundWindow(hWnd);
    }

    private static IntPtr MakeLParam(int low, int high)
    {
        return (IntPtr)((high << 16) | (low & 0xFFFF));
    }
}
