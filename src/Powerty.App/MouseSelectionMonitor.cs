using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;

namespace Powerty.App;

internal sealed class MouseSelectionMonitor : IDisposable
{
    private const int WhMouseLl = 14;
    private const int HcAction = 0;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const uint LlmhfInjected = 0x00000001;
    private const int DragThresholdPixels = 8;

    // Test hook: injected (synthetic) mouse input is normally ignored so the tool never
    // reacts to scripted clicks; setting POWERTY_ALLOW_INJECTED=1 enables end-to-end testing.
    private static readonly bool AllowInjected =
        Environment.GetEnvironmentVariable("POWERTY_ALLOW_INJECTED") == "1";

    private readonly HookProcedure _hookProcedure;
    private IntPtr _hookHandle;
    private NativePoint _dragStartPoint;
    private NativePoint _lastDownPoint;
    private long _lastDownTicks;
    private bool _dragging;
    private bool _sawDoubleClick;

    public MouseSelectionMonitor()
    {
        _hookProcedure = OnMouseEvent;
    }

    public event Action? PointerPressed;

    /// <summary>Raised on the hook's owning thread with the drag start/end points and the
    /// tick count at which the release was seen, so the dispatcher hop can be measured.</summary>
    public event Action<Point, Point, long>? DragSelectCompleted;

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        _hookHandle = SetWindowsHookEx(WhMouseLl, _hookProcedure, GetModuleHandle(null), 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动全局鼠标监测。");
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
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        Start();
    }

    public void Dispose() => Stop();

    private IntPtr OnMouseEvent(int code, IntPtr message, IntPtr data)
    {
        if (code == HcAction)
        {
            var eventType = message.ToInt32();
            if (eventType is WmLButtonDown or WmLButtonUp or WmLButtonDblClk)
            {
                var mouseData = Marshal.PtrToStructure<MouseData>(data);
                if (AllowInjected || (mouseData.Flags & LlmhfInjected) == 0)
                {
                    if (eventType == WmLButtonDown)
                    {
                        // Own double-click detection (time + distance): many window classes
                        // never emit WM_LBUTTONDBLCLK, so the system message alone is not enough.
                        var now = Environment.TickCount64;
                        var deltaX0 = mouseData.Point.X - _lastDownPoint.X;
                        var deltaY0 = mouseData.Point.Y - _lastDownPoint.Y;
                        _sawDoubleClick = now - _lastDownTicks <= (long)GetDoubleClickTime() + 80 &&
                                          (deltaX0 * deltaX0) + (deltaY0 * deltaY0) <= 6 * 6;
                        _lastDownTicks = now;
                        _lastDownPoint = mouseData.Point;

                        _dragging = true;
                        _dragStartPoint = mouseData.Point;
                        PointerPressed?.Invoke();
                    }
                    else if (eventType == WmLButtonDblClk)
                    {
                        // Second press of a double-click: the word under the cursor is now
                        // selected, so the following button-up counts as a selection gesture.
                        _dragging = true;
                        _dragStartPoint = mouseData.Point;
                        _sawDoubleClick = true;
                    }
                    else if (_dragging)
                    {
                        _dragging = false;
                        var wasDoubleClick = _sawDoubleClick;
                        _sawDoubleClick = false;
                        var deltaX = mouseData.Point.X - _dragStartPoint.X;
                        var deltaY = mouseData.Point.Y - _dragStartPoint.Y;
                        var end = new Point(mouseData.Point.X, mouseData.Point.Y);
                        if ((deltaX * deltaX) + (deltaY * deltaY) >= DragThresholdPixels * DragThresholdPixels)
                        {
                            DragSelectCompleted?.Invoke(new Point(_dragStartPoint.X, _dragStartPoint.Y), end, Environment.TickCount64);
                        }
                        else if (wasDoubleClick)
                        {
                            // Word/paragraph selection via double (or triple) click.
                            DragSelectCompleted?.Invoke(end, end, Environment.TickCount64);
                        }
                    }
                }
            }
        }

        return CallNextHookEx(_hookHandle, code, message, data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        public NativePoint Point;
        public uint MouseDataValue;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
