using System.Windows;
using System.Windows.Media;

namespace Qwaacty.App;

/// <summary>A tiny non-activating preview shown next to the input box while the user
/// types: it displays the current round's text and its translation. It never takes
/// focus, so typing continues uninterrupted underneath.</summary>
public partial class TranslatePreviewWindow : Window
{
    private static readonly Brush TranslationBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
    private static readonly Brush TranslatingBrush = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85));

    public TranslatePreviewWindow()
    {
        InitializeComponent();
    }

    public void ShowPreview(string source, string? translation, bool translating, string hotkeyChord)
    {
        HotkeyHintText.Text = $"{hotkeyChord} 写入";
        SourceText.Text = Shorten(source, 80);
        TranslationText.Text = translating ? "翻译中…" : Shorten(translation ?? string.Empty, 200);
        TranslationText.Foreground = translating ? TranslatingBrush : TranslationBrush;
        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
    }

    public void UpdateTranslation(string translation)
    {
        TranslationText.Foreground = TranslationBrush;
        TranslationText.Text = Shorten(translation, 200);
        UpdateLayout();
    }

    /// <summary>Places the preview beside the input box: right of it when the screen has
    /// room, below otherwise, above as the last resort for bottom-edge inputs.</summary>
    public void PositionNear(Rect anchor)
    {
        UpdateLayout();
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 120;
        var left = anchor.Right + 10;
        var top = anchor.Top;
        if (left + width > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 8)
        {
            left = anchor.Left;
            top = anchor.Bottom + 8;
        }

        top = Math.Clamp(top, SystemParameters.VirtualScreenTop + 8,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - height - 8);
        left = Math.Clamp(left, SystemParameters.VirtualScreenLeft + 8,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - width - 8);
        Left = left;
        Top = top;
    }

    public void HidePreview()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    private static string Shorten(string value, int limit)
    {
        var squashed = value.Replace("\r", " ").Replace("\n", " ").Trim();
        if (squashed.Length <= limit)
        {
            return squashed;
        }

        var cut = limit;
        if (cut > 0 && char.IsHighSurrogate(squashed[cut - 1]))
        {
            cut--;
        }

        return squashed[..cut] + "…";
    }
}
