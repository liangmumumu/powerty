using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Powerty.Core;

namespace Powerty.App;

public partial class LookupWindow : Window
{
    private const string PhoneticFont = "Segoe UI";
    private const int MaxCollapsedChips = 8;

    private static readonly Brush WordBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush PhoneticBrush = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE));
    private static readonly Brush PartOfSpeechBrush = new SolidColorBrush(Color.FromRgb(0xD7, 0xBA, 0x7D));
    private static readonly Brush SeparatorBrush = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42));
    private static readonly Brush ChipTextBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly Brush ChipBackgroundBrush = CreateFrozenBrush(0x2D, 0x2D, 0x30);
    private static readonly Brush ChipHoverBrush = CreateFrozenBrush(0x37, 0x37, 0x3D);

    private string _fontFamilyName = "Segoe UI, Microsoft YaHei UI";
    private double _fontSize = 14;
    private double _opacity = 0.95;
    private double _maxContentHeight = 440;
    private double _preferredWidth = 760;
    private Color _backgroundColor = Color.FromRgb(0x1E, 0x1E, 0x1E);
    private Brush _textBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
    private Rect? _typingAnchor;
    private Rect? _typingInputBounds;
    private string? _typingSourceQuery;
    private bool _compactTypingMode;
    private bool _typingPopupMode;
    private IReadOnlyList<DictionaryEntry> _lastEntries = [];
    private string? _messageText;
    private bool _streamMode;
    private int _expandedIndex;
    private bool _userMoved;
    private string? _aiTitle;
    private string? _aiText;
    private bool _aiError;
    private bool _typingStatusMode;
    private readonly List<RetainedRow> _retainedRows = [];
    private readonly Border _aiSeparator;
    private readonly Border _aiDivider;
    private readonly TextBlock _aiTitleBlock;
    private readonly TextBlock _aiTextBlock;
    private bool _aiCompactLayout;
    private bool _fitScheduled;
    private string _lastFitDetail = "fit=?";

    private static readonly Brush AiErrorBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0x87, 0x71));

    /// <summary>A concise word/translation pair kept at the top of the typing popup.</summary>
    public sealed record RetainedRow(string Word, string Gloss);

    public LookupWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        // Preview (tunneling) so body presses are seen before any child handles them;
        // MouseLeftButtonDown is a direct event and never bubbles up to the window.
        PreviewMouseLeftButtonDown += OnWindowPreviewMouseLeftButtonDown;
        // Reused across streaming updates: recreating these on every chunk made the UI
        // thread stall often enough to delay the low-level input hooks (mouse stutter).
        _aiSeparator = new Border { Height = 1, Background = SeparatorBrush, Margin = new Thickness(0, 10, 0, 8) };
        _aiDivider = new Border { Height = 1, Background = SeparatorBrush, Margin = new Thickness(0, 6, 0, 6) };
        _aiTitleBlock = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        _aiTextBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };
        ApplyAppearanceCore();
    }

    public event Action<double, double>? SizeAdjusted;

    public void ApplyAppearance(string fontFamily, double fontSize, double opacity, string backgroundColor, string textColor)
    {
        _fontFamilyName = string.IsNullOrWhiteSpace(fontFamily)
            ? "Segoe UI, Microsoft YaHei UI"
            : $"{fontFamily}, Microsoft YaHei UI, Segoe UI";
        _fontSize = Math.Clamp(fontSize, 10, 26);
        _opacity = Math.Clamp(opacity, 0.35, 1.0);
        _backgroundColor = ParseColor(backgroundColor, Color.FromRgb(0x1E, 0x1E, 0x1E));
        _textBrush = new SolidColorBrush(ParseColor(textColor, Color.FromRgb(0xD4, 0xD4, 0xD4)));
        ApplyAppearanceCore();
        if (_messageText is not null)
        {
            RenderMessage();
        }
        else if (_lastEntries.Count > 0)
        {
            RenderEntries(keepScrollPosition: true);
        }

        RenderAiSection();
        RenderRetainedRows();
        if (IsVisible)
        {
            ScheduleFitToContent();
        }
    }

    public void ApplyLayout(double width, double maxContentHeight)
    {
        Width = Math.Clamp(width, 280, 1200);
        _preferredWidth = Width;
        _maxContentHeight = Math.Clamp(maxContentHeight, 40, 1000);
        Height = Math.Clamp(_maxContentHeight + 60, 100, 1060);
        ContentScroll.MaxHeight = _maxContentHeight + ScrollPaddingVertical();
    }

    private void ApplyAppearanceCore()
    {
        TextElement.SetFontFamily(RootBorder, new FontFamily(_fontFamilyName));
        var alpha = (byte)Math.Round(_opacity * 255);
        RootBorder.Background = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(alpha * _backgroundColor.A / 255.0),
            _backgroundColor.R,
            _backgroundColor.G,
            _backgroundColor.B));
        _aiTextBlock.Foreground = _textBrush;
    }

    public void ShowEntries(
        IReadOnlyList<DictionaryEntry> entries,
        bool compactTyping = false,
        Rect? typingAnchor = null,
        Rect? typingInputBounds = null,
        string? typingSourceQuery = null)
    {
        var stopwatch = Stopwatch.StartNew();
        _messageText = null;
        _lastEntries = entries;
        _expandedIndex = 0;
        _typingStatusMode = false;
        _compactTypingMode = compactTyping;
        _typingPopupMode = compactTyping;
        _typingSourceQuery = compactTyping ? typingSourceQuery : null;
        _typingAnchor = compactTyping ? typingAnchor ?? typingInputBounds : null;
        _typingInputBounds = compactTyping ? typingInputBounds : null;
        ClearAiSection();
        RenderEntries(keepScrollPosition: false);
        var wasVisible = IsVisible;
        if (!wasVisible)
        {
            Show();
        }

        FitToContent();
        UpdateLayout();
        if (!wasVisible || compactTyping)
        {
            PositionNearCursor();
        }

        LogSlowShow("entries", stopwatch);
    }

    public void ShowMessage(
        string title,
        string message,
        bool typingPopup = false,
        Rect? typingAnchor = null,
        Rect? typingInputBounds = null)
    {
        var stopwatch = Stopwatch.StartNew();
        _lastEntries = [];
        _messageText = message;
        _streamMode = false;
        _typingStatusMode = false;
        _compactTypingMode = false;
        _typingPopupMode = typingPopup;
        _typingAnchor = typingPopup ? typingAnchor ?? typingInputBounds : null;
        _typingInputBounds = typingPopup ? typingInputBounds : null;
        ClearAiSection();
        HeaderText.Text = title;
        RenderMessage();
        var wasVisible = IsVisible;
        if (!wasVisible)
        {
            Show();
        }

        FitToContent();
        UpdateLayout();
        if (!wasVisible || typingPopup)
        {
            PositionNearCursor();
        }

        LogSlowShow("message", stopwatch);
    }

    /// <summary>Typing pad: the detail area shows that the user is composing while the
    /// retained word strip (if any) stays visible above it.</summary>
    public void ShowTypingStatus(
        string text,
        Rect? typingAnchor = null,
        Rect? typingInputBounds = null,
        bool typingPopup = true)
    {
        var stopwatch = Stopwatch.StartNew();
        _lastEntries = [];
        _messageText = text;
        _streamMode = false;
        _typingStatusMode = true;
        _compactTypingMode = false;
        _typingPopupMode = typingPopup;
        _typingAnchor = typingPopup ? typingAnchor ?? typingInputBounds : null;
        _typingInputBounds = typingPopup ? typingInputBounds : null;
        ClearAiSection();
        HeaderText.Text = "键入翻译";
        RenderMessage();
        var wasVisible = IsVisible;
        if (!wasVisible)
        {
            Show();
        }

        FitToContent();
        UpdateLayout();
        if (!wasVisible)
        {
            PositionNearCursor();
        }

        LogSlowShow("typing-status", stopwatch);
    }

    public void SetRetainedRows(IReadOnlyList<RetainedRow> rows)
    {
        _retainedRows.Clear();
        _retainedRows.AddRange(rows);
        RenderRetainedRows();
        if (IsVisible)
        {
            ScheduleFitToContent();
        }
    }

    private void RenderRetainedRows()
    {
        RetainedPanel.Children.Clear();
        if (_retainedRows.Count == 0)
        {
            return;
        }

        foreach (var row in _retainedRows)
        {
            var line = new TextBlock
            {
                FontSize = Math.Max(10, _fontSize - 1),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = Math.Max(10, _fontSize - 1) * 1.4
            };
            line.Inlines.Add(new Run($"{row.Word}  ") { FontWeight = FontWeights.SemiBold, Foreground = WordBrush });
            line.Inlines.Add(new Run(row.Gloss) { Foreground = ChipTextBrush });
            RetainedPanel.Children.Add(line);
        }

        RetainedPanel.Children.Add(new Border
        {
            Height = 1,
            Background = SeparatorBrush,
            Margin = new Thickness(0, 6, 0, 2)
        });
    }

    public void ShowStreamingText(string title, string text)
    {
        var stopwatch = Stopwatch.StartNew();
        _lastEntries = [];
        _messageText = text;
        _streamMode = true;
        _compactTypingMode = false;
        _typingPopupMode = false;
        _typingAnchor = null;
        _typingInputBounds = null;
        HeaderText.Text = title;
        RenderMessage();
        if (!IsVisible)
        {
            Show();
        }

        FitToContent();
        UpdateLayout();
        PositionNearCursor();

        LogSlowShow("streaming", stopwatch);
    }

    public void UpdateStreamingText(string text)
    {
        if (!_streamMode || !IsVisible)
        {
            return;
        }

        _messageText = text;
        RenderMessage();
        UpdateLayout();
        ContentScroll.ScrollToEnd();
        ScheduleFitToContent();
    }

    /// <summary>Updates the AI translation block shown above dictionary entries.</summary>
    public void SetAiSection(string? title, string? text, bool isError = false)
    {
        _aiTitle = title;
        _aiText = text;
        _aiError = isError;
        RenderAiSection();
        if (IsVisible)
        {
            ScheduleFitToContent();
        }
    }

    public void ClearAiSection() => SetAiSection(null, null);

    private void RenderAiSection()
    {
        if (_aiText is null)
        {
            AiPanel.Children.Clear();
            _aiCompactLayout = false;
            ContentScroll.Padding = new Thickness(14, 2, 10, 12);
            _aiSeparator.Visibility = Visibility.Visible;
            _aiTitleBlock.Visibility = Visibility.Visible;
            _aiTextBlock.ToolTip = null;
            return;
        }

        _aiTitleBlock.Text = _aiTitle ?? "AI 增强翻译";
        _aiTitleBlock.FontSize = Math.Max(10, _fontSize - 1);
        _aiTitleBlock.Foreground = _aiError ? AiErrorBrush : PhoneticBrush;
        var sharesSpaceWithEntries = _lastEntries.Count > 0;
        ContentScroll.Padding = sharesSpaceWithEntries
            ? new Thickness(14, 0, 10, 2)
            : new Thickness(14, 2, 10, 12);
        _aiSeparator.Visibility = sharesSpaceWithEntries ? Visibility.Collapsed : Visibility.Visible;
        _aiTitleBlock.Visibility = sharesSpaceWithEntries ? Visibility.Collapsed : Visibility.Visible;
        _aiTextBlock.Inlines.Clear();
        if (sharesSpaceWithEntries)
        {
            _aiTextBlock.Inlines.Add(new Run($"{_aiTitle ?? "AI 增强翻译"}：")
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = _aiError ? AiErrorBrush : PhoneticBrush
            });
            _aiTextBlock.Inlines.Add(new Run(_aiText) { Foreground = _textBrush });
            _aiTextBlock.TextWrapping = TextWrapping.NoWrap;
            _aiTextBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            _aiTextBlock.Margin = new Thickness(0);
            _aiTextBlock.ToolTip = _aiText;
            _aiTextBlock.FontSize = Math.Max(11, _fontSize - 3);
            _aiTextBlock.LineHeight = _aiTextBlock.FontSize * 1.25;
        }
        else
        {
            _aiTextBlock.Text = _aiText;
            _aiTextBlock.TextWrapping = TextWrapping.Wrap;
            _aiTextBlock.TextTrimming = TextTrimming.None;
            _aiTextBlock.Margin = new Thickness(0);
            _aiTextBlock.ToolTip = null;
            _aiTextBlock.FontSize = _fontSize + 2;
            _aiTextBlock.LineHeight = (_fontSize + 2) * 1.45;
        }
        _aiTextBlock.Foreground = _textBrush;

        if (AiPanel.Children.Count == 0 || _aiCompactLayout != sharesSpaceWithEntries)
        {
            AiPanel.Children.Clear();
            if (sharesSpaceWithEntries)
            {
                // Single-line AI summary sitting directly above the dictionary
                // results: keep a light divider so the two sections stay apart.
                AiPanel.Children.Add(_aiTextBlock);
                AiPanel.Children.Add(_aiDivider);
            }
            else
            {
                AiPanel.Children.Add(_aiSeparator);
                AiPanel.Children.Add(_aiTitleBlock);
                AiPanel.Children.Add(_aiTextBlock);
            }

            _aiCompactLayout = sharesSpaceWithEntries;
        }
    }

    private void RenderMessage()
    {
        EntriesPanel.Children.Clear();
        ChipsPanel.Children.Clear();
        EntriesPanel.Children.Add(new TextBlock
        {
            Text = _messageText,
            FontSize = _fontSize,
            FontStyle = _typingStatusMode ? FontStyles.Italic : FontStyles.Normal,
            Foreground = _typingStatusMode ? ChipTextBrush : _textBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = _fontSize * 1.5
        });
        RenderAiSection();
    }

    private void RenderEntries(bool keepScrollPosition)
    {
        _messageText = null;
        _streamMode = false;
        var scrollOffset = keepScrollPosition ? ContentScroll.VerticalOffset : 0;
        EntriesPanel.Children.Clear();
        ChipsPanel.Children.Clear();
        if (_lastEntries.Count == 0)
        {
            return;
        }

        _expandedIndex = Math.Clamp(_expandedIndex, 0, _lastEntries.Count - 1);
        var expanded = _lastEntries[_expandedIndex];
        var reverseTypingQuery = _compactTypingMode &&
            expanded.Direction == LookupDirection.ChineseToEnglish &&
            !string.IsNullOrWhiteSpace(_typingSourceQuery);
        HeaderText.Text = reverseTypingQuery
            ? $"{_typingSourceQuery}（英译）"
            : _lastEntries.Count > 1
            ? $"{expanded.Query}（{_expandedIndex + 1}/{_lastEntries.Count}）"
            : expanded.Query;

        RenderExpandedEntry(expanded);

        if (_lastEntries.Count > 1)
        {
            var chipsShown = 0;
            for (var index = 0; index < _lastEntries.Count && chipsShown < MaxCollapsedChips; index++)
            {
                if (index == _expandedIndex)
                {
                    continue;
                }

                chipsShown++;
                ChipsPanel.Children.Add(CreateChip(_lastEntries[index], index));
            }
        }

        UpdateLayout();
        if (keepScrollPosition)
        {
            ContentScroll.ScrollToVerticalOffset(scrollOffset);
        }
        else
        {
            ContentScroll.ScrollToTop();
        }

        if (_userMoved)
        {
            ClampToScreen();
        }

        RenderAiSection();
    }

    private void RenderExpandedEntry(DictionaryEntry entry)
    {
        if (_compactTypingMode && entry.Direction == LookupDirection.ChineseToEnglish)
        {
            EntriesPanel.Children.Add(new TextBlock
            {
                Text = _typingSourceQuery ?? entry.Query,
                FontSize = _fontSize + 4,
                FontWeight = FontWeights.SemiBold,
                Foreground = WordBrush,
                TextWrapping = TextWrapping.Wrap
            });
            EntriesPanel.Children.Add(new TextBlock
            {
                Text = $"英：{entry.Query}",
                Margin = new Thickness(0, 2, 0, 0),
                FontSize = _fontSize + 2,
                Foreground = _textBrush,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = (_fontSize + 2) * 1.45
            });

            var compactReverseGroups = DefinitionFormatter.Format(entry.Definitions);
            if (compactReverseGroups.Count > 0)
            {
                AddDefinitionGroup(compactReverseGroups[0].PartOfSpeech, compactReverseGroups[0].Fragments);
            }
            else
            {
                var compactReverseDefinition = entry.Definitions.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (compactReverseDefinition is not null)
                {
                    AddParagraphs([compactReverseDefinition]);
                }
            }

            return;
        }

        EntriesPanel.Children.Add(new TextBlock
        {
            Text = entry.Query,
            FontSize = _fontSize + (_compactTypingMode ? 4 : 3),
            FontWeight = FontWeights.SemiBold,
            Foreground = WordBrush,
            TextWrapping = TextWrapping.Wrap
        });

        if (_compactTypingMode)
        {
            var compactGroups = DefinitionFormatter.Format(entry.Definitions);
            if (compactGroups.Count > 0)
            {
                AddDefinitionGroup(compactGroups[0].PartOfSpeech, compactGroups[0].Fragments);
            }
            else
            {
                var definition = entry.Definitions.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (definition is not null)
                {
                    AddParagraphs([definition]);
                }
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(entry.Phonetic))
        {
            EntriesPanel.Children.Add(new TextBlock
            {
                Text = entry.Phonetic,
                Margin = new Thickness(0, 2, 0, 0),
                FontFamily = new FontFamily(PhoneticFont),
                FontSize = _fontSize - 1,
                Foreground = PhoneticBrush,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (!entry.Structured)
        {
            AddParagraphs(entry.Definitions);
            return;
        }

        var groups = DefinitionFormatter.Format(entry.Definitions);
        var displayedFragments = new HashSet<string>(StringComparer.Ordinal);
        var displayedAnyGroup = false;
        foreach (var group in groups)
        {
            var fragments = group.Fragments
                .Select(fragment =>
                {
                    var key = GlossTokenizer.DedupKey(fragment);
                    return (Fragment: fragment, Key: key.Length == 0 ? fragment.ToLowerInvariant() : key);
                })
                .Where(item => displayedFragments.Add(item.Key))
                .Select(item => item.Fragment)
                .ToArray();
            if (fragments.Length == 0)
            {
                continue;
            }

            displayedAnyGroup = true;
            AddDefinitionGroup(group.PartOfSpeech, fragments);
        }

        if (!displayedAnyGroup)
        {
            AddParagraphs(entry.Definitions);
        }
    }

    private void AddParagraphs(IReadOnlyList<string> definitions)
    {
        foreach (var definition in definitions)
        {
            EntriesPanel.Children.Add(new TextBlock
            {
                Text = definition,
                Margin = new Thickness(0, 4, 0, 0),
                FontSize = _fontSize,
                Foreground = _textBrush,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = _fontSize * 1.45
            });
        }
    }

    private void AddDefinitionGroup(string partOfSpeech, IReadOnlyList<string> fragments)
    {
        var row = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (partOfSpeech.Length > 0)
        {
            var tag = new TextBlock
            {
                Text = partOfSpeech,
                FontSize = Math.Max(10, _fontSize),
                FontWeight = FontWeights.SemiBold,
                Foreground = PartOfSpeechBrush,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(tag, 0);
            row.Children.Add(tag);
        }

        var gloss = new TextBlock
        {
            Text = string.Join("；", fragments),
            FontSize = _fontSize + 2,
            Foreground = _textBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = (_fontSize + 2) * 1.45
        };
        Grid.SetColumn(gloss, 1);
        row.Children.Add(gloss);
        EntriesPanel.Children.Add(row);
    }

    private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private Border CreateChip(DictionaryEntry entry, int index)
    {
        var label = new TextBlock
        {
            Text = entry.Query,
            FontSize = Math.Max(10, _fontSize - 2),
            Foreground = ChipTextBrush
        };

        var chip = new Border
        {
            Background = ChipBackgroundBrush,
            BorderBrush = SeparatorBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 4, 6, 0),
            Cursor = Cursors.Hand,
            Tag = index,
            Child = label
        };

        chip.MouseLeftButtonUp += OnChipClicked;
        chip.MouseEnter += (_, _) => chip.Background = ChipHoverBrush;
        chip.MouseLeave += (_, _) => chip.Background = ChipBackgroundBrush;
        return chip;
    }

    private void OnChipClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: int index })
        {
            _expandedIndex = index;
            RenderEntries(keepScrollPosition: false);
            ScheduleFitToContent();
            e.Handled = true;
        }
    }

    private void ClampToScreen()
    {
        var popupWidth = ActualWidth > 0 ? ActualWidth : MinWidth;
        var popupHeight = ActualHeight > 0 ? ActualHeight : 100;
        Left = Math.Clamp(Left, SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - popupWidth);
        Top = Math.Clamp(Top, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - popupHeight);
    }

    /// <summary>Vertical space the scroll viewer consumes inside its own element (its
    /// padding): the viewport available to the content is smaller than the element height
    /// by this amount. MaxHeight must include it, otherwise the first layout is always
    /// short by the padding and the self-correcting loop needs a second full pass.</summary>
    private double ScrollPaddingVertical() => ContentScroll.Padding.Top + ContentScroll.Padding.Bottom;

    /// <summary>Shrink-wraps the popup around the current content: everything stays visible
    /// without scrolling while the box remains as small as the content allows (bounded by
    /// the preferred width and the virtual screen).</summary>
    private void FitToContent()
    {
        var stopwatch = Stopwatch.StartNew();
        var layoutPasses = 1;
        var horizontalChrome = RootBorder.BorderThickness.Left + RootBorder.BorderThickness.Right +
                               ContentScroll.Padding.Left + ContentScroll.Padding.Right + 2;
        var headerHeight = HeaderRow.ActualHeight > 0 ? HeaderRow.ActualHeight : 28;
        var verticalChrome = RootBorder.BorderThickness.Top + RootBorder.BorderThickness.Bottom +
                             HeaderRow.Margin.Top + HeaderRow.Margin.Bottom + headerHeight +
                             ContentScroll.Padding.Top + ContentScroll.Padding.Bottom + 2;

        var naturalWidth = Math.Max(NaturalWidth(RetainedPanel),
            Math.Max(NaturalWidth(EntriesPanel), NaturalWidth(ChipsPanel)));
        if (!_aiCompactLayout)
        {
            naturalWidth = Math.Max(naturalWidth, NaturalWidth(AiPanel));
        }

        var maxWidth = Math.Max(MinWidth, Math.Min(_preferredWidth, SystemParameters.VirtualScreenWidth * 0.98));
        var targetWidth = Math.Clamp(naturalWidth + horizontalChrome, MinWidth, maxWidth);
        Width = targetWidth;

        ContentScroll.MaxHeight = double.PositiveInfinity;
        ContentStack.Measure(new Size(Math.Max(1, targetWidth - horizontalChrome), double.PositiveInfinity));
        var requiredContentHeight = ContentStack.DesiredSize.Height;
        var maxContentHeight = Math.Max(60, SystemParameters.VirtualScreenHeight * 0.96 - verticalChrome);
        var fittedContentHeight = Math.Min(requiredContentHeight, maxContentHeight);
        ContentScroll.MaxHeight = fittedContentHeight + ScrollPaddingVertical();
        Height = Math.Clamp(fittedContentHeight + verticalChrome, MinHeight, SystemParameters.VirtualScreenHeight * 0.96);

        // WPF measure results can disagree with the arranged layout by a few pixels
        // (rounding, wrap points, chrome metrics); grow until the scroll viewer reports
        // that the whole content fits, so nothing is ever clipped.
        UpdateLayout();
        for (var pass = 0; pass < 2; pass++)
        {
            var overflow = ContentScroll.ExtentHeight - ContentScroll.ViewportHeight;
            if (overflow <= 0.5)
            {
                break;
            }

            layoutPasses++;
            ContentScroll.MaxHeight += overflow + 1;
            Height = Math.Min(Height + overflow + 1, SystemParameters.VirtualScreenHeight * 0.96);
            UpdateLayout();
        }

        stopwatch.Stop();
        _lastFitDetail = $"fit={stopwatch.Elapsed.TotalMilliseconds:F1}ms req={requiredContentHeight:F0} final={fittedContentHeight:F0} passes={layoutPasses} ev={ContentScroll.ExtentHeight:F0}/{ContentScroll.ViewportHeight:F0}";
        if (stopwatch.ElapsedMilliseconds >= 4)
        {
            DiagnosticsLog.Write("UI", $"slow {_lastFitDetail} w={Width:F0} h={Height:F0}");
        }
    }

    /// <summary>Refits the popup and keeps it inside the screen when the size changed.</summary>
    private void FitToContentAndReposition()
    {
        if (!IsVisible)
        {
            FitToContent();
            return;
        }

        var previousWidth = ActualWidth;
        var previousHeight = ActualHeight;
        FitToContent();
        UpdateLayout();
        if (Math.Abs(ActualWidth - previousWidth) > 0.5 || Math.Abs(ActualHeight - previousHeight) > 0.5)
        {
            if (_typingPopupMode && _typingAnchor is { IsEmpty: false } anchor)
            {
                PositionNearTypingAnchor(anchor, _typingInputBounds);
            }
            else
            {
                ClampToScreen();
            }
        }
    }

    /// <summary>Coalesces fit requests into a single dispatcher callback so bursts of
    /// content updates (AI streaming chunks) cannot stall the input pipeline with
    /// repeated synchronous layouts — the mouse hook stays responsive.</summary>
    private void ScheduleFitToContent()
    {
        if (_fitScheduled)
        {
            return;
        }

        _fitScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _fitScheduled = false;
            FitToContentAndReposition();
        });
    }

    private void LogSlowShow(string kind, Stopwatch stopwatch)
    {
        if (stopwatch.ElapsedMilliseconds >= 6)
        {
            DiagnosticsLog.Write("UI", $"slow show {kind}: {stopwatch.Elapsed.TotalMilliseconds:F1} ms {_lastFitDetail}");
        }
    }

    private static double NaturalWidth(UIElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    private void PositionNearCursor()
    {
        if (_typingPopupMode && _typingAnchor is { } anchor && !anchor.IsEmpty)
        {
            PositionNearTypingAnchor(anchor, _typingInputBounds);
            return;
        }

        if (_userMoved && !_compactTypingMode)
        {
            ClampToScreen();
            return;
        }

        var cursor = WindowsCursor.GetPosition();
        // GetCursorPos reports device pixels while Left/Top are in DIPs; convert so the
        // popup tracks the cursor correctly at non-100% display scaling.
        var deviceToDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        if (deviceToDip.HasValue)
        {
            var logical = deviceToDip.Value.Transform(new Point(cursor.X, cursor.Y));
            cursor = new Point(logical.X, logical.Y);
        }

        var popupWidth = ActualWidth > 0 ? ActualWidth : MinWidth;
        var popupHeight = ActualHeight > 0 ? ActualHeight : MinHeight;
        Left = Math.Clamp(cursor.X + 18, SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - popupWidth);
        Top = Math.Clamp(cursor.Y + 18, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - popupHeight);
    }

    private void PositionNearTypingAnchor(Rect anchor, Rect? inputBounds)
    {
        var deviceToDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        if (deviceToDip.HasValue)
        {
            anchor.Transform(deviceToDip.Value);
            if (inputBounds.HasValue && !inputBounds.Value.IsEmpty)
            {
                var bounds = inputBounds.Value;
                bounds.Transform(deviceToDip.Value);
                inputBounds = bounds;
            }
        }

        var popupWidth = ActualWidth > 0 ? ActualWidth : Width;
        var popupHeight = ActualHeight > 0 ? ActualHeight : MinHeight;
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        var popupRectWidth = Math.Min(popupWidth, SystemParameters.VirtualScreenWidth);
        var popupRectHeight = Math.Min(popupHeight, SystemParameters.VirtualScreenHeight);
        var target = inputBounds is { IsEmpty: false } inputRect ? inputRect : anchor;
        var candidates = new[]
        {
            new Rect(target.Left, target.Bottom + 8, popupRectWidth, popupRectHeight),
            new Rect(target.Left, target.Top - popupRectHeight - 8, popupRectWidth, popupRectHeight),
            new Rect(target.Right + 8, anchor.Top, popupRectWidth, popupRectHeight),
            new Rect(target.Left - popupRectWidth - 8, anchor.Top, popupRectWidth, popupRectHeight),
            new Rect(anchor.Left, anchor.Bottom + 8, popupRectWidth, popupRectHeight),
            new Rect(anchor.Left, anchor.Top - popupRectHeight - 8, popupRectWidth, popupRectHeight)
        };

        var safeCandidate = candidates.Where(candidate =>
            candidate.Left >= screenLeft &&
            candidate.Top >= screenTop &&
            candidate.Right <= screenRight &&
            candidate.Bottom <= screenBottom &&
            !candidate.IntersectsWith(anchor) &&
            (!inputBounds.HasValue || inputBounds.Value.IsEmpty || !candidate.IntersectsWith(inputBounds.Value)))
            .Select(candidate => (Rect?)candidate)
            .FirstOrDefault();

        var selected = safeCandidate ?? candidates.Where(candidate =>
                candidate.Left >= screenLeft &&
                candidate.Top >= screenTop &&
                candidate.Right <= screenRight &&
                candidate.Bottom <= screenBottom &&
                !candidate.IntersectsWith(anchor))
            .Select(candidate => (Rect?)candidate)
            .FirstOrDefault();

        if (!selected.HasValue)
        {
            selected = new Rect(
                Math.Clamp(anchor.Left, screenLeft, screenRight - popupRectWidth),
                Math.Clamp(anchor.Bottom + 8, screenTop, screenBottom - popupRectHeight),
                popupRectWidth,
                popupRectHeight);
        }

        Left = selected.Value.Left;
        Top = selected.Value.Top;
        _userMoved = false;
    }

    private void ResizeThumb_OnDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var edge = (sender as FrameworkElement)?.Tag as string;
        if (edge is "Right" or "Corner")
        {
            Width = Math.Clamp(Width + e.HorizontalChange, 280, 1200);
            _preferredWidth = Width;
        }

        if (edge is "Bottom" or "Corner")
        {
            Height = Math.Clamp(Height + e.VerticalChange, 100, 1060);
            _maxContentHeight = Math.Clamp(Height - 60, 40, 1000);
            ContentScroll.MaxHeight = _maxContentHeight + ScrollPaddingVertical();
        }

        ClampToScreen();
        e.Handled = true;
    }

    private void ResizeThumb_OnDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        SizeAdjusted?.Invoke(Width, _maxContentHeight);
        e.Handled = true;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Hide();

    private void OnWindowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (!IsWithinHeader(e.OriginalSource as DependencyObject) ||
                IsInteractiveSource(e.OriginalSource as DependencyObject))
            {
                return;
            }

            e.Handled = true;
            DragMove();
            _userMoved = true;
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception)
        {
            // Never let a stray exception from input handling take down the app.
        }
    }

    private bool IsWithinHeader(DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, HeaderRow))
            {
                return true;
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    private static bool IsInteractiveSource(DependencyObject? source)
    {
        while (source is not null)
        {
            switch (source)
            {
                case ScrollBar or Thumb or ButtonBase:
                    return true;
                case FrameworkElement { Name: "ChipsPanel" }:
                    return true;
            }

            // Runs/Hyperlinks are ContentElements; VisualTreeHelper throws for those.
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    private static Color ParseColor(string value, Color fallback)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch (FormatException)
        {
            return fallback;
        }
        catch (InvalidCastException)
        {
            return fallback;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowProcedure);
        }
    }

    private static IntPtr WindowProcedure(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0021)
        {
            handled = true;
            return new IntPtr(3);
        }

        return IntPtr.Zero;
    }
}

internal static class WindowsCursor
{
    public static Point GetPosition()
    {
        if (!GetCursorPos(out var point))
        {
            return new Point(SystemParameters.PrimaryScreenWidth / 2, SystemParameters.PrimaryScreenHeight / 2);
        }

        return new Point(point.X, point.Y);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}
