using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Qwaacty.App;

public partial class ScreenshotOverlayWindow : Window
{
    private Point _startPoint;
    private bool _dragging;

    /// <summary>Raised with the selection in DIPs relative to the overlay, then the overlay closes itself.</summary>
    public event Action<Rect>? RegionSelected;

    public ScreenshotOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Cover the entire virtual screen (all monitors) in device pixels; WPF's
        // Left/Top would have to be converted from DIPs which is error-prone on
        // mixed-DPI setups, so position the native window directly.
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(
            handle,
            HwndTopmost,
            GetSystemMetrics(SmXVirtualScreen),
            GetSystemMetrics(SmYVirtualScreen),
            GetSystemMetrics(SmCxVirtualScreen),
            GetSystemMetrics(SmCyVirtualScreen),
            SwpShowWindow);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _startPoint = e.GetPosition(this);
        _dragging = true;
        SelectionRect.Visibility = Visibility.Visible;
        UpdateSelection(_startPoint);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            UpdateSelection(e.GetPosition(this));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        var selection = BuildSelection(_startPoint, e.GetPosition(this));
        if (selection.Width >= 8 && selection.Height >= 8)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            RegionSelected?.Invoke(selection);
            var signalMs = stopwatch.ElapsedMilliseconds;
            stopwatch.Restart();
            Close();
            DiagnosticsLog.Write("SHOT", $"release handler: signal {signalMs} ms, close {stopwatch.ElapsedMilliseconds} ms");
        }
        else
        {
            // Treat a stray click as "try again" instead of dismissing the overlay.
            SelectionRect.Visibility = Visibility.Collapsed;
        }

        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Close();
        e.Handled = true;
    }

    private void UpdateSelection(Point current)
    {
        var selection = BuildSelection(_startPoint, current);
        SelectionRect.Width = selection.Width;
        SelectionRect.Height = selection.Height;
        System.Windows.Controls.Canvas.SetLeft(SelectionRect, selection.Left);
        System.Windows.Controls.Canvas.SetTop(SelectionRect, selection.Top);
    }

    private static Rect BuildSelection(Point start, Point end) =>
        new(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
