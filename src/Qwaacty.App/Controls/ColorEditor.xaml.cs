using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Qwaacty.App.Controls;

/// <summary>
/// A compact colour editor: one swatch + hex box + red/green/blue/alpha sliders.
/// Slider changes raise <see cref="ColorChanged"/> live so callers can preview;
/// the hex box accepts typed values on Enter or focus loss.
/// </summary>
public partial class ColorEditor : UserControl
{
    private bool _updating;

    public ColorEditor()
    {
        InitializeComponent();
    }

    /// <summary>Raised whenever the user changes the colour (sliders or hex box).</summary>
    public event EventHandler<Color>? ColorChanged;

    /// <summary>The currently selected colour.</summary>
    public Color SelectedColor { get; private set; } = Color.FromRgb(0x1E, 0x1E, 0x1E);

    /// <summary>Sets the colour without raising <see cref="ColorChanged"/> (initial load).</summary>
    public void SetColor(Color color)
    {
        _updating = true;
        try
        {
            RedSlider.Value = color.R;
            GreenSlider.Value = color.G;
            BlueSlider.Value = color.B;
            AlphaSlider.Value = color.A;
            ApplyFromSliders(raise: false);
        }
        finally
        {
            _updating = false;
        }
    }

    private void Slider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating)
        {
            return;
        }

        ApplyFromSliders(raise: true);
    }

    private void ApplyFromSliders(bool raise)
    {
        var color = Color.FromArgb(
            (byte)Math.Round(AlphaSlider.Value),
            (byte)Math.Round(RedSlider.Value),
            (byte)Math.Round(GreenSlider.Value),
            (byte)Math.Round(BlueSlider.Value));
        SelectedColor = color;
        RedValueText.Text = color.R.ToString();
        GreenValueText.Text = color.G.ToString();
        BlueValueText.Text = color.B.ToString();
        AlphaValueText.Text = $"{color.A / 255.0 * 100:F0}%";
        SwatchBorder.Background = new SolidColorBrush(color);
        HexTextBox.Text = color.ToString();
        if (raise)
        {
            ColorChanged?.Invoke(this, color);
        }
    }

    private void HexTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        CommitHex();
        e.Handled = true;
    }

    private void HexTextBox_OnLostFocus(object sender, RoutedEventArgs e) => CommitHex();

    private void CommitHex()
    {
        if (_updating)
        {
            return;
        }

        if (TryParseHex(HexTextBox.Text, out var color))
        {
            if (color != SelectedColor)
            {
                SetColor(color);
                ColorChanged?.Invoke(this, color);
            }
            else
            {
                HexTextBox.Text = SelectedColor.ToString();
            }
        }
        else
        {
            // Invalid input: restore the current value instead of leaving a broken string.
            HexTextBox.Text = SelectedColor.ToString();
        }
    }

    private static bool TryParseHex(string value, out Color color)
    {
        color = default;
        var text = value.Trim();
        if (text.Length is not (4 or 7 or 9) || text[0] != '#')
        {
            return false;
        }

        try
        {
            if (ColorConverter.ConvertFromString(text) is not Color parsed)
            {
                return false;
            }

            color = parsed;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }
}
