using System.Runtime.InteropServices;

namespace Qwaacty.App;

/// <summary>Types synthetic keyboard input for the input-translation module: deletes the
/// original segment with backspaces, then writes the translation as Unicode characters
/// (works with CJK and punctuation without an IME). Held modifiers are released around
/// the injection so Ctrl/Alt from the trigger hotkey cannot turn the backspaces into
/// word-deletes or the letters into shortcuts.</summary>
internal static class TextInjector
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const ushort VkBack = 0x08;
    private const ushort VkReturn = 0x0D;
    private const ushort VkTab = 0x09;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkLWin = 0x5B;
    private const ushort VkRWin = 0x5C;
    private const int MaxBatchInputs = 64;

    /// <summary>Sends the given number of Backspace presses. Returns false when the
    /// send failed (e.g. blocked by a higher-integrity window).</summary>
    public static bool SendBackspaces(int count)
    {
        for (var sent = 0; sent < count;)
        {
            var batch = Math.Min(MaxBatchInputs / 2, count - sent);
            var inputs = new Input[batch * 2];
            for (var index = 0; index < batch; index++)
            {
                inputs[index * 2] = CreateVirtualKey(VkBack, keyUp: false);
                inputs[index * 2 + 1] = CreateVirtualKey(VkBack, keyUp: true);
            }

            if (!SendInputAll(inputs))
            {
                return false;
            }

            sent += batch;
        }

        return true;
    }

    /// <summary>Releases every modifier that is physically held; returns the ones that
    /// were down so <see cref="RestoreModifiers"/> can re-press them afterwards.</summary>
    public static List<ushort> ReleaseHeldModifiers()
    {
        var held = new List<ushort>(4);
        foreach (var key in (ReadOnlySpan<ushort>)[VkControl, VkMenu, VkShift, VkLWin, VkRWin])
        {
            if (IsKeyDown(key))
            {
                held.Add(key);
                SendInputAll([CreateVirtualKey(key, keyUp: true)]);
            }
        }

        return held;
    }

    /// <summary>Re-presses modifiers released by <see cref="ReleaseHeldModifiers"/> so
    /// the physical keyboard state and Windows' view stay consistent.</summary>
    public static void RestoreModifiers(List<ushort> held)
    {
        foreach (var key in held)
        {
            SendInputAll([CreateVirtualKey(key, keyUp: false)]);
        }
    }

    /// <summary>Types text as Unicode characters. Newlines become Enter presses because
    /// apps expect a virtual key rather than a control character. Returns false when a
    /// send failed.</summary>
    public static bool SendUnicodeText(string text)
    {
        var pending = new List<Input>(MaxBatchInputs);
        var ok = true;

        void Flush()
        {
            if (pending.Count == 0)
            {
                return;
            }

            if (!SendInputAll(pending.ToArray()))
            {
                ok = false;
            }

            pending.Clear();
        }

        foreach (var character in text)
        {
            if (character == '\r')
            {
                continue;
            }

            if (character is '\n' or '\t')
            {
                Flush();
                var key = character == '\n' ? VkReturn : VkTab;
                pending.Add(CreateVirtualKey(key, keyUp: false));
                pending.Add(CreateVirtualKey(key, keyUp: true));
            }
            else
            {
                pending.Add(CreateUnicode(character, keyUp: false));
                pending.Add(CreateUnicode(character, keyUp: true));
            }

            if (pending.Count >= MaxBatchInputs)
            {
                Flush();
            }
        }

        Flush();
        return ok;
    }

    private static bool SendInputAll(Input[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;

    private static bool IsKeyDown(ushort virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static Input CreateVirtualKey(ushort virtualKey, bool keyUp) => new()
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

    private static Input CreateUnicode(char character, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = 0,
                ScanCode = character,
                Flags = (keyUp ? KeyEventKeyUp : 0) | KeyEventUnicode,
                Time = 0,
                ExtraInfo = IntPtr.Zero
            }
        }
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

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
        public int X;
        public int Y;
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
        public ushort ParamLow;
        public ushort ParamHigh;
    }
}
