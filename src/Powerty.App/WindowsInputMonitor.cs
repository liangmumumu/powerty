using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Powerty.App;

internal sealed class WindowsInputMonitor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int HcAction = 0;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const uint LlKfInjected = 0x00000010;

    // Test hook: injected (synthetic) keyboard input is normally ignored so the tool never
    // reacts to scripted typing; setting POWERTY_ALLOW_INJECTED=1 enables end-to-end testing.
    private static readonly bool AllowInjected =
        Environment.GetEnvironmentVariable("POWERTY_ALLOW_INJECTED") == "1";

    private const uint VkBack = 0x08;
    private const uint VkTab = 0x09;
    private const uint VkReturn = 0x0D;
    private const uint VkEscape = 0x1B;
    private const uint VkPrior = 0x21;
    private const uint VkNext = 0x22;
    private const uint VkEnd = 0x23;
    private const uint VkHome = 0x24;
    private const uint VkLeft = 0x25;
    private const uint VkUp = 0x26;
    private const uint VkRight = 0x27;
    private const uint VkDown = 0x28;
    private const uint VkDelete = 0x2E;
    private const uint VkProcessKey = 0xE5;
    private const uint VkLWin = 0x5B;
    private const uint VkRWin = 0x5C;

    private readonly HookProcedure _hookProcedure;
    private IntPtr _hookHandle;
    private IntPtr _lastForegroundWindow;

    public WindowsInputMonitor()
    {
        _hookProcedure = OnKeyboardEvent;
    }

    public bool CaptureCharacters { get; set; }

    public event Action? InputActivity;

    public event Action<char>? CharacterTyped;

    public event Action? BackspaceTyped;

    public event Action? ContextResetRequested;

    public event Action? LineTerminated;

    public event Action? ForegroundChanged;

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        _lastForegroundWindow = IntPtr.Zero;
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProcedure, GetModuleHandle(null), 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动全局键盘活动监测。");
        }
    }

    public bool IsRunning => _hookHandle != IntPtr.Zero;

    public void Stop()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        // A failed unhook usually means Windows already silently removed the hook
        // (it drops low-level hooks when the owning thread stalls). Drop the stale
        // handle either way so a later Start()/Refresh() can install a fresh hook.
        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    public void Refresh()
    {
        // Reinstalling the hook must not look like a foreground change to the next
        // keystroke: the watchdog refresh runs every 20 s and resetting this would
        // wipe the typing buffer mid-sentence on every refresh.
        var lastForeground = _lastForegroundWindow;
        Stop();
        Start();
        _lastForegroundWindow = lastForeground;
    }

    public void Dispose() => Stop();

    // Reused buffers: the hook callback must stay allocation-free (it runs for every
    // keystroke on the UI thread).
    private readonly byte[] _keyboardStateBuffer = new byte[256];
    private readonly StringBuilder _characterBuffer = new StringBuilder(8);

    private IntPtr OnKeyboardEvent(int code, IntPtr message, IntPtr data)
    {
        // The callback arrives via reverse P/Invoke from user32: a managed exception
        // escaping it would terminate the process (and skip CallNextHookEx).
        try
        {
            return OnKeyboardEventCore(code, message, data);
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Write("MON", $"keyboard hook callback failed: {exception.GetType().Name} {exception.Message}");
            return CallNextHookEx(_hookHandle, code, message, data);
        }
    }

    private IntPtr OnKeyboardEventCore(int code, IntPtr message, IntPtr data)
    {
        if (code == HcAction && (message.ToInt32() == WmKeyDown || message.ToInt32() == WmSysKeyDown))
        {
            var keyData = Marshal.PtrToStructure<KeyboardData>(data);
            if (AllowInjected || (keyData.Flags & LlKfInjected) == 0)
            {
                TrackForegroundWindow();
                if (CaptureCharacters)
                {
                    ProcessCharacter(keyData);
                }

                InputActivity?.Invoke();
            }
        }

        return CallNextHookEx(_hookHandle, code, message, data);
    }

    private void TrackForegroundWindow()
    {
        var foreground = GetForegroundWindow();
        if (foreground != _lastForegroundWindow)
        {
            _lastForegroundWindow = foreground;
            ContextResetRequested?.Invoke();
            ForegroundChanged?.Invoke();
        }
    }

    private void ProcessCharacter(KeyboardData keyData)
    {
        switch (keyData.VirtualKey)
        {
            case VkBack:
                BackspaceTyped?.Invoke();
                return;
            case VkReturn:
                LineTerminated?.Invoke();
                ContextResetRequested?.Invoke();
                return;
            case VkTab:
            case VkEscape:
            case VkPrior:
            case VkNext:
            case VkEnd:
            case VkHome:
            case VkLeft:
            case VkUp:
            case VkRight:
            case VkDown:
            case VkDelete:
            case VkProcessKey:
                ContextResetRequested?.Invoke();
                return;
        }

        if (IsAsyncKeyDown(0x11) || IsAsyncKeyDown(0x12) || IsAsyncKeyDown(VkLWin) || IsAsyncKeyDown(VkRWin))
        {
            return;
        }

        if (!GetKeyboardState(_keyboardStateBuffer))
        {
            return;
        }

        _characterBuffer.Clear();
        var count = ToUnicodeEx(keyData.VirtualKey, keyData.ScanCode, _keyboardStateBuffer, _characterBuffer, _characterBuffer.Capacity, 0, GetKeyboardLayout(0));
        for (var index = 0; index < count && index < _characterBuffer.Length; index++)
        {
            var character = _characterBuffer[index];
            if (!char.IsControl(character))
            {
                CharacterTyped?.Invoke(character);
            }
        }
    }

    private static bool IsAsyncKeyDown(uint virtualKey) => (GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] keyState);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState, StringBuilder characters, int characterCount, uint flags, IntPtr keyboardLayout);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
