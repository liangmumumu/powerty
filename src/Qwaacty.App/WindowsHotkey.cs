using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Qwaacty.App;

internal sealed class WindowsHotkey(IntPtr windowHandle, int hotkeyId)
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWindows = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private uint _currentVirtualKey;
    private uint _currentModifiers;

    public string CurrentChord { get; private set; } = string.Empty;

    public bool IsChordHeld()
    {
        if (_currentVirtualKey == 0)
        {
            return false;
        }

        if (IsKeyDown((int)_currentVirtualKey))
        {
            return true;
        }

        return ((_currentModifiers & ModControl) != 0 && IsKeyDown(VkControl)) ||
               ((_currentModifiers & ModShift) != 0 && IsKeyDown(VkShift)) ||
               ((_currentModifiers & ModAlt) != 0 && IsKeyDown(VkMenu)) ||
               ((_currentModifiers & ModWindows) != 0 && (IsKeyDown(VkLWin) || IsKeyDown(VkRWin)));
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Register(string chord)
    {
        var (modifiers, virtualKey) = Parse(chord);
        if (!RegisterHotKey(windowHandle, hotkeyId, modifiers | ModNoRepeat, virtualKey))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "快捷键已被占用，或无法注册。");
        }

        CurrentChord = chord;
        _currentVirtualKey = virtualKey;
        _currentModifiers = modifiers;
    }

    public void Unregister()
    {
        if (string.IsNullOrEmpty(CurrentChord))
        {
            return;
        }

        if (!UnregisterHotKey(windowHandle, hotkeyId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注销全局快捷键。");
        }

        CurrentChord = string.Empty;
        _currentVirtualKey = 0;
        _currentModifiers = 0;
    }

    private static (uint Modifiers, uint VirtualKey) Parse(string chord)
    {
        var tokens = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            throw new FormatException("快捷键需由至少一个修饰键和一个按键组成，例如 Ctrl+Shift+Space。");
        }

        uint modifiers = 0;
        Key? key = null;
        foreach (var token in tokens)
        {
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModControl;
            }
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModAlt;
            }
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModShift;
            }
            else if (token.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModWindows;
            }
            else if (Enum.TryParse<Key>(token, ignoreCase: true, out var parsedKey) &&
                     parsedKey is not Key.None and not Key.System and not Key.LeftCtrl and not Key.RightCtrl
                         and not Key.LeftAlt and not Key.RightAlt and not Key.LeftShift and not Key.RightShift
                         and not Key.LWin and not Key.RWin)
            {
                if (key.HasValue)
                {
                    throw new FormatException("快捷键只能包含一个普通按键。");
                }

                key = parsedKey;
            }
            else
            {
                throw new FormatException($"无法识别快捷键部分“{token}”。");
            }
        }

        if (modifiers == 0 || !key.HasValue)
        {
            throw new FormatException("快捷键需包含修饰键和一个普通按键。");
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        if (virtualKey == 0)
        {
            throw new FormatException("无法将该按键转换为 Windows 快捷键。");
        }

        return (modifiers, virtualKey);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
