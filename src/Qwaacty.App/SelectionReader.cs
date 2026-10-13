using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace Qwaacty.App;

internal sealed record SelectionReadResult(bool Success, string? Text, string? Message);

internal static class SelectionReader
{
    private const int MaxSelectionCharacters = 240;

    public static Task<SelectionReadResult> ReadAsync()
    {
        var completion = new TaskCompletionSource<SelectionReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(ReadCore());
            }
            catch (Exception exception)
            {
                completion.SetResult(new SelectionReadResult(false, null, $"读取选中文本失败：{exception.Message}"));
            }
        })
        {
            IsBackground = true,
            Name = "Qwaacty.SelectionReader"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static int _automationAttemptPending;

    private static SelectionReadResult ReadCore()
    {
        // Fast password pre-check with a short budget; UIA can block for seconds in busy
        // apps. When UIA cannot answer (null), fall back to the classic ES_PASSWORD window
        // style so "unknown" is treated as unsafe where we can actually tell.
        var passwordCheck = CheckFocusedElementPassword();
        if (passwordCheck == true || (passwordCheck is null && HasPasswordWindowStyle()))
        {
            return new SelectionReadResult(false, null, "当前为密码输入区域，已跳过。");
        }

        // Clipboard copy is the primary path: bounded latency, works across
        // virtually all applications, and the previous clipboard is restored.
        var clipboardResult = TryReadViaClipboard();
        if (clipboardResult.Success)
        {
            DiagnosticsLog.Write("READ", $"clipboard ok len={clipboardResult.Text?.Length ?? 0}");
            return clipboardResult;
        }

        DiagnosticsLog.Write("READ", $"clipboard fail: {clipboardResult.Message}");

        // Fall back to UI Automation, bounded by a timeout, only when no previous
        // automation attempt is still hanging in the background.
        if (Interlocked.CompareExchange(ref _automationAttemptPending, 1, 0) != 0)
        {
            return clipboardResult;
        }

        var automationTask = Task.Run(() =>
        {
            var ok = TryReadViaAutomation(out var text, out var blocked);
            return (Ok: ok, Text: text, Blocked: blocked);
        });
        bool automationCompleted;
        try
        {
            automationCompleted = automationTask.Wait(TimeSpan.FromMilliseconds(800));
        }
        catch (AggregateException)
        {
            // The task faulted before the timeout: it is finished, so fall through and
            // release the latch below instead of leaving the UIA fallback disabled for
            // the rest of the session.
            automationCompleted = true;
        }

        if (automationCompleted)
        {
            Interlocked.Exchange(ref _automationAttemptPending, 0);
            if (automationTask.IsCompletedSuccessfully)
            {
                var result = automationTask.Result;
                if (result.Blocked)
                {
                    DiagnosticsLog.Write("READ", "uia blocked (password)");
                    return new SelectionReadResult(false, null, result.Text);
                }

                if (result.Ok && !string.IsNullOrWhiteSpace(result.Text))
                {
                    DiagnosticsLog.Write("READ", $"uia ok len={result.Text.Length}");
                    return new SelectionReadResult(true, result.Text, null);
                }
            }

            DiagnosticsLog.Write("READ", "uia no selection");
        }
        else
        {
            DiagnosticsLog.Write("READ", "uia timeout (800ms, background task continues)");
            _ = automationTask.ContinueWith(
                _ => Interlocked.Exchange(ref _automationAttemptPending, 0),
                TaskScheduler.Default);
        }

        return clipboardResult;
    }

    private static bool? CheckFocusedElementPassword()
    {
        try
        {
            var task = Task.Run(() =>
            {
                try
                {
                    var focused = AutomationElement.FocusedElement;
                    if (focused is null)
                    {
                        // UIA could not answer ("unknown", e.g.微信/QQ/Electron 或超时):
                        // report null so the caller's ES_PASSWORD fallback runs instead of
                        // failing open.
                        return (bool?)null;
                    }

                    if (focused.Current.ProcessId == Environment.ProcessId)
                    {
                        return (bool?)false;
                    }

                    var value = focused.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, ignoreDefaultValue: true);
                    return value is bool flag ? flag : (bool?)null;
                }
                catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException or NotSupportedException)
                {
                    return null;
                }
            });
            if (task.Wait(TimeSpan.FromMilliseconds(300)) && task.IsCompletedSuccessfully)
            {
                return task.Result;
            }
        }
        catch (AggregateException)
        {
        }

        return null;
    }

    private static bool TryReadViaAutomation(out string? text, out bool blocked)
    {
        text = null;
        blocked = false;
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null || focused.Current.ProcessId == Environment.ProcessId)
            {
                return false;
            }

            var passwordValue = focused.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, ignoreDefaultValue: true);
            if (passwordValue is bool isPassword && isPassword)
            {
                blocked = true;
                text = "当前为密码输入区域，已跳过。";
                return false;
            }

            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) ||
                patternObject is not TextPattern textPattern)
            {
                return false;
            }

            var selections = textPattern.GetSelection();
            if (selections.Length != 1)
            {
                return false;
            }

            var selected = selections[0].GetText(MaxSelectionCharacters)?.Trim();
            if (string.IsNullOrWhiteSpace(selected))
            {
                return false;
            }

            text = selected;
            return true;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static SelectionReadResult TryReadViaClipboard()
    {
        object? previousData = null;
        var previousRead = false;
        try
        {
            if (Clipboard.GetDataObject() is { } dataObject)
            {
                previousData = dataObject;
            }

            previousRead = true;
        }
        catch (COMException)
        {
        }

        string? captured = null;
        uint capturedSequence = 0;
        var sendFailed = false;
        var foregroundAtStart = GetForegroundWindow();
        var foreignClipboardWrite = false;
        // In console windows Ctrl+C doubles as a SIGINT (killing the user's build /
        // session); Ctrl+Insert is the safe copy chord there. Terminals and IDE hosts
        // (embedded terminals) get the same treatment.
        var preferInsert = IsTerminalLikeWindow(foregroundAtStart);
        for (var attempt = 0; attempt < 2 && captured is null; attempt++)
        {
            // Only inject the copy chord while the window that received the gesture is
            // still in front: once a capture tool's overlay has closed, a late injected
            // Ctrl+Insert would land in an unrelated app and could copy its text over
            // content (e.g. a fresh screenshot) the user just put on the clipboard.
            if (GetForegroundWindow() != foregroundAtStart)
            {
                DiagnosticsLog.Write("READ", "copy chord skipped: foreground window changed");
                break;
            }

            if (attempt == 1 && foreignClipboardWrite)
            {
                // The first wait window already saw the clipboard rewritten to non-text
                // content by another app; poking more keystrokes around would risk
                // replacing that content.
                DiagnosticsLog.Write("READ", "copy chord skipped: clipboard rewritten by another app");
                break;
            }

            var sequenceBefore = GetClipboardSequenceNumber();
            if (!SendCopyShortcut(attempt, preferInsert))
            {
                sendFailed = true;
                break;
            }

            var deadline = Environment.TickCount64 + (attempt == 0 ? 800 : 700);
            while (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(20);
                if (GetClipboardSequenceNumber() == sequenceBefore)
                {
                    continue;
                }

                try
                {
                    if (Clipboard.ContainsText())
                    {
                        var current = Clipboard.GetText();
                        if (!string.IsNullOrWhiteSpace(current))
                        {
                            captured = current;
                            capturedSequence = GetClipboardSequenceNumber();
                            break;
                        }
                    }
                    else if (attempt == 0)
                    {
                        // Non-text content appeared while we waited for our own copy:
                        // most likely another app (e.g. a screenshot tool) just wrote to
                        // the clipboard — do not inject anything else.
                        foreignClipboardWrite = true;
                    }
                }
                catch (COMException)
                {
                }
            }
        }

        // Restore the user's clipboard ONLY when this read actually replaced it with
        // the copied selection. When the copy failed, the clipboard may now hold
        // content another app wrote just now (e.g. a screenshot tool putting an image) —
        // restoring or clearing here would destroy it. Even after a successful capture
        // the restore is skipped when yet another clipboard write raced in afterwards.
        if (captured is not null && GetClipboardSequenceNumber() == capturedSequence)
        {
            try
            {
                if (previousData is not null)
                {
                    Clipboard.SetDataObject(previousData, true);
                }
                else if (previousRead)
                {
                    Clipboard.Clear();
                }
            }
            catch (COMException exception)
            {
                DiagnosticsLog.Write("READ", $"clipboard restore failed: {exception.Message}");
            }
            catch (ExternalException exception)
            {
                DiagnosticsLog.Write("READ", $"clipboard restore failed: {exception.Message}");
            }
        }

        if (sendFailed)
        {
            return new SelectionReadResult(false, null, "无法发送复制按键。");
        }

        if (string.IsNullOrWhiteSpace(captured))
        {
            return new SelectionReadResult(false, null, "未读取到选中的文本。请先用鼠标选中文本（文字可被复制），再按快捷键重试。");
        }

        return new SelectionReadResult(true, captured.Trim(), null);
    }

    private static bool SendCopyShortcut(int attempt, bool preferInsert)
    {
        // First attempt: Ctrl+C (or Ctrl+Insert in consoles). Second attempt: Ctrl+Insert
        // (works in several legacy controls).
        // The trigger hotkey is usually still physically held; held Shift/Alt/Win would turn the
        // copy chord into something else, so the whole sequence temporarily releases those
        // modifiers, sends the copy chord, and restores them - all in one SendInput batch so
        // the target app observes a clean Ctrl+C (or Ctrl+Insert) keystroke.
        var letterKey = attempt == 0 && !preferInsert ? VkC : VkInsert;
        var inputs = new List<Input>(8);
        var restored = new List<int>(4);
        foreach (var virtualKey in HeldNonControlModifiers())
        {
            restored.Add(virtualKey);
            inputs.Add(CreateKeyboardInput((ushort)virtualKey, keyUp: true));
        }

        var controlHeld = IsKeyDown(VkControl);
        if (!controlHeld)
        {
            inputs.Add(CreateKeyboardInput(VkControl, keyUp: false));
        }

        inputs.Add(CreateKeyboardInput(letterKey, keyUp: false));
        inputs.Add(CreateKeyboardInput(letterKey, keyUp: true));

        if (!controlHeld)
        {
            inputs.Add(CreateKeyboardInput(VkControl, keyUp: true));
        }

        foreach (var virtualKey in restored)
        {
            inputs.Add(CreateKeyboardInput((ushort)virtualKey, keyUp: false));
        }

        var array = inputs.ToArray();
        return SendInput((uint)array.Length, array, Marshal.SizeOf<Input>()) == array.Length;
    }

    private static List<int> HeldNonControlModifiers()
    {
        const int vkShift = 0x10;
        const int vkMenu = 0x12;
        const int vkLWin = 0x5B;
        const int vkRWin = 0x5C;
        var held = new List<int>(4);
        if (IsKeyDown(vkShift))
        {
            held.Add(vkShift);
        }

        if (IsKeyDown(vkMenu))
        {
            held.Add(vkMenu);
        }

        if (IsKeyDown(vkLWin))
        {
            held.Add(vkLWin);
        }

        if (IsKeyDown(vkRWin))
        {
            held.Add(vkRWin);
        }

        return held;
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static Input CreateKeyboardInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                ScanCode = 0,
                Flags = keyUp ? KeyEventKeyUp : 0,
                Time = 0,
                ExtraInfo = IntPtr.Zero
            }
        }
    };

    private const ushort VkControl = 0x11;
    private const ushort VkC = 0x43;
    private const ushort VkInsert = 0x2D;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    internal static bool IsConsoleWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var buffer = new StringBuilder(64);
        var length = GetClassName(handle, buffer, buffer.Capacity);
        if (length <= 0)
        {
            return false;
        }

        var className = buffer.ToString(0, length);
        return className is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "mintty"
            or "VirtualConsole" or "Alacritty"
            || className.StartsWith("PuTTY", StringComparison.Ordinal);
    }

    /// <summary>Terminal hosts whose window class is not enough to identify them: IDE
    /// windows (VS Code, JetBrains, Visual Studio…) embed terminals inside a generic
    /// window, and GPU terminal emulators use non-console window classes. Their input
    /// may be shell input/passwords and Ctrl+C may reach the shell as SIGINT, so they
    /// are treated like terminals.</summary>
    private static readonly HashSet<string> TerminalHostProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "code - insiders", "cursor", "windsurf", "vscodium",
        "devenv", "idea64", "pycharm64", "goland64", "clion64", "webstorm64", "rider64", "rustrover64",
        "wezterm-gui", "kitty", "alacritty", "tabby", "conemu64", "conemu", "cmder", "mobaxterm"
    };

    internal static bool IsTerminalLikeWindow(IntPtr handle)
    {
        if (IsConsoleWindow(handle))
        {
            return true;
        }

        var processName = GetProcessName(handle);
        return processName is not null && TerminalHostProcessNames.Contains(processName);
    }

    private static string? GetProcessName(IntPtr handle)
    {
        _ = GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Non-UIA password check for the current foreground window, used when UI
    /// Automation cannot verify the focused field.</summary>
    internal static bool IsForegroundPasswordField() => HasPasswordWindowStyle();

    private static bool HasPasswordWindowStyle()
    {
        // A top-level window never carries ES_PASSWORD (the style lives on the focused
        // edit child), so ask the GUI thread for the actual focus HWND and check its
        // style — walking a couple of parents for custom controls that focus a wrapper.
        const int gwlStyle = -16;
        const int esPassword = 0x0020;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var threadId = GetWindowThreadProcessId(foreground, out _);
        if (threadId == 0)
        {
            return false;
        }

        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(threadId, ref info) || info.Focus == IntPtr.Zero)
        {
            return false;
        }

        var walker = info.Focus;
        for (var depth = 0; depth < 4 && walker != IntPtr.Zero; depth++)
        {
            if ((GetWindowLong(walker, gwlStyle) & esPassword) != 0)
            {
                return true;
            }

            walker = GetParent(walker);
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public NativeRect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int DeltaX;
        public int DeltaY;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }
}
