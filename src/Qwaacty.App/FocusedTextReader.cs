using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Qwaacty.App;

internal sealed record FocusedTextReadResult(
    bool IsSafe,
    string? Context,
    int CaretIndex,
    string? Message,
    Rect? AnchorBounds = null,
    Rect? InputBounds = null,
    bool PasswordBlocked = false);

internal sealed record SelectionContextReadResult(bool Ok, string? Context, string? Message);

/// <summary>What the input-translation module reads: the tail before the caret plus the
/// total length (for round segmenting), or the manual selection when there is one.</summary>
internal sealed record TypingSegmentReadResult(
    bool Ok,
    bool PasswordBlocked,
    bool IsSelection,
    int LengthBeforeCaret,
    bool LengthCapped,
    string Tail,
    string? SelectedText,
    char? PrecedingChar,
    Rect? InputBounds,
    IntPtr ForegroundWindow,
    string? Message);

internal static class FocusedTextReader
{
    private const int ContextWindowCharacters = 48;
    private const int TypingSegmentReadCharacters = 2000;
    private const int TypingSegmentLengthProbe = 20000;

    public static FocusedTextReadResult Read()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                return Unsafe("无法确认当前输入位置，已暂停查词。");
            }

            var processId = focused.Current.ProcessId;
            if (processId == Environment.ProcessId)
            {
                return Unsafe("设置窗口处于焦点状态。");
            }

            var passwordValue = focused.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, ignoreDefaultValue: true);
            if (passwordValue is bool isPassword && isPassword)
            {
                return new FocusedTextReadResult(false, null, 0, "当前为密码输入区域，已暂停查词。", PasswordBlocked: true);
            }

            if (passwordValue is not bool)
            {
                return Unsafe("当前输入区域无法安全识别，已暂停查词。");
            }

            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) ||
                patternObject is not TextPattern textPattern)
            {
                return ReadViaValuePattern(focused);
            }

            var selections = textPattern.GetSelection();
            if (selections.Length != 1)
            {
                return Unsafe("当前输入位置无法安全确认，已暂停查词。");
            }

            var selection = selections[0];
            if (selection.CompareEndpoints(
                    TextPatternRangeEndpoint.Start,
                    selection,
                    TextPatternRangeEndpoint.End) != 0)
            {
                return Unsafe("选中文本时暂不查词。");
            }

            var contextRange = selection.Clone();
            var movedLeft = contextRange.MoveEndpointByUnit(
                TextPatternRangeEndpoint.Start,
                TextUnit.Character,
                -ContextWindowCharacters);
            contextRange.MoveEndpointByUnit(
                TextPatternRangeEndpoint.End,
                TextUnit.Character,
                ContextWindowCharacters);

            var context = contextRange.GetText(ContextWindowCharacters * 2);
            if (context.Length > ContextWindowCharacters * 2)
            {
                return Unsafe("输入上下文超出安全读取范围。");
            }

            var inputBounds = focused.Current.BoundingRectangle;
            return new(
                true,
                context,
                -movedLeft,
                null,
                GetCaretBounds(selection, focused),
                inputBounds.IsEmpty ? null : inputBounds);
        }
        catch (ElementNotAvailableException)
        {
            return Unsafe("输入控件已变化，已暂停查词。");
        }
        catch (InvalidOperationException)
        {
            return Unsafe("当前应用无法安全提供输入上下文，已暂停查词。");
        }
        catch (COMException)
        {
            return Unsafe("无法安全读取当前输入上下文，已暂停查词。");
        }
        catch (ArgumentException)
        {
            return Unsafe("当前文本控件返回了无效范围，已暂停查词。");
        }
        catch (NotSupportedException)
        {
            return Unsafe("当前文本控件不支持安全读取，已暂停查词。");
        }
    }

    private static FocusedTextReadResult ReadViaValuePattern(AutomationElement focused)
    {
        if (!focused.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) ||
            valueObject is not ValuePattern valuePattern)
        {
            return Unsafe("当前应用未提供可安全读取的文本上下文。");
        }

        var value = valuePattern.Current.Value;
        if (value is null)
        {
            return Unsafe("当前应用未提供可安全读取的文本上下文。");
        }

        var context = value.Length > ContextWindowCharacters * 2
            ? value[^(ContextWindowCharacters * 2)..]
            : value;
        var bounds = focused.Current.BoundingRectangle;
        return new(true, context, context.Length, null, bounds.IsEmpty ? null : bounds, bounds.IsEmpty ? null : bounds);
    }

    private static Rect? GetCaretBounds(TextPatternRange selection, AutomationElement focused)
    {
        try
        {
            var rectangles = selection.GetBoundingRectangles();
            if (rectangles.Length > 0 &&
                !rectangles[0].IsEmpty &&
                double.IsFinite(rectangles[0].X) &&
                double.IsFinite(rectangles[0].Y) &&
                double.IsFinite(rectangles[0].Width) &&
                double.IsFinite(rectangles[0].Height) &&
                rectangles[0].Height > 0)
            {
                return rectangles[0];
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
        }

        var bounds = focused.Current.BoundingRectangle;
        return bounds.IsEmpty ? null : bounds;
    }

    private static FocusedTextReadResult Unsafe(string message) => new(false, null, 0, message);

    private const int SelectionContextWindowCharacters = 160;
    private const int SelectionContextMaxCharacters = 400;

    /// <summary>
    /// Reads text surrounding the active selection for the AI context-enhance feature.
    /// Unlike <see cref="Read"/>, a non-empty selection is expected (and is fine).
    /// </summary>
    public static SelectionContextReadResult ReadContextAroundSelection(string selectionText)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                return SelectionContextUnavailable("无法确认当前输入位置。");
            }

            if (focused.Current.ProcessId == Environment.ProcessId)
            {
                return SelectionContextUnavailable("焦点在本程序窗口。");
            }

            var passwordValue = focused.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, ignoreDefaultValue: true);
            if (passwordValue is not bool isPassword || isPassword)
            {
                return SelectionContextUnavailable("当前输入区域无法安全读取。");
            }

            if (focused.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) &&
                patternObject is TextPattern textPattern)
            {
                return ReadContextViaTextPattern(textPattern);
            }

            if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) &&
                valueObject is ValuePattern valuePattern)
            {
                return ReadContextViaValuePattern(valuePattern.Current.Value, selectionText);
            }

            return SelectionContextUnavailable("当前应用未提供文本上下文。");
        }
        catch (ElementNotAvailableException)
        {
            return SelectionContextUnavailable("输入控件已变化。");
        }
        catch (InvalidOperationException)
        {
            return SelectionContextUnavailable("当前应用无法提供文本上下文。");
        }
        catch (COMException)
        {
            return SelectionContextUnavailable("无法读取当前文本上下文。");
        }
        catch (ArgumentException)
        {
            return SelectionContextUnavailable("文本控件返回了无效范围。");
        }
        catch (NotSupportedException)
        {
            return SelectionContextUnavailable("当前文本控件不支持读取。");
        }
    }

    private static SelectionContextReadResult ReadContextViaTextPattern(TextPattern textPattern)
    {
        TextPatternRange[] selections;
        try
        {
            selections = textPattern.GetSelection();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
            return SelectionContextUnavailable("无法获取当前选中范围。");
        }

        if (selections.Length == 0)
        {
            return SelectionContextUnavailable("当前没有可用的选中范围。");
        }

        var range = selections[0].Clone();
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -SelectionContextWindowCharacters);
        range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, SelectionContextWindowCharacters);
        var text = range.GetText(SelectionContextMaxCharacters);
        return string.IsNullOrWhiteSpace(text)
            ? SelectionContextUnavailable("选中范围周围没有可读文本。")
            : new SelectionContextReadResult(true, SquashContext(text), null);
    }

    private static SelectionContextReadResult ReadContextViaValuePattern(string? value, string selectionText)
    {
        if (string.IsNullOrEmpty(value))
        {
            return SelectionContextUnavailable("输入框内容为空。");
        }

        // The whole value doubles as context; centre a window on the selection when possible.
        var scan = value.Length > 4000 ? value[..4000] : value;
        var index = selectionText.Length == 0
            ? -1
            : scan.IndexOf(selectionText, StringComparison.Ordinal);
        if (index < 0)
        {
            // Fall back to the true end of the document, not the end of the truncated
            // scan window (that would send mid-document text as "context").
            var tail = value.Length > SelectionContextMaxCharacters ? value[^SelectionContextMaxCharacters..] : value;
            return new SelectionContextReadResult(true, SquashContext(tail), null);
        }

        var start = Math.Max(0, index - SelectionContextWindowCharacters);
        var end = Math.Min(scan.Length, index + selectionText.Length + SelectionContextWindowCharacters);
        return new SelectionContextReadResult(true, SquashContext(scan[start..end]), null);
    }

    private static string SquashContext(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var character in text)
        {
            var isSpace = char.IsWhiteSpace(character);
            if (isSpace)
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(character);
            }

            lastWasSpace = isSpace;
        }

        var squashed = builder.ToString().Trim();
        return squashed.Length > SelectionContextMaxCharacters
            ? squashed[..SelectionContextMaxCharacters]
            : squashed;
    }

    private static SelectionContextReadResult SelectionContextUnavailable(string message) => new(false, null, message);

    /// <summary>Reads what the input-translation module needs: the text before the caret
    /// (tail plus total length) or, when text is selected, the selection itself.</summary>
    public static TypingSegmentReadResult ReadTypingSegment()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                return TypingSegmentFailure("无法确认当前输入位置。");
            }

            if (focused.Current.ProcessId == Environment.ProcessId)
            {
                return TypingSegmentFailure("当前焦点在本程序窗口。");
            }

            var passwordValue = focused.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, ignoreDefaultValue: true);
            if (passwordValue is bool isPassword && isPassword)
            {
                return new TypingSegmentReadResult(false, true, false, 0, false, string.Empty, null, null, null,
                    GetForegroundWindow(), "当前为密码输入区域，已跳过。");
            }

            if (passwordValue is not bool)
            {
                return TypingSegmentFailure("当前输入区域无法安全读取。");
            }

            if (focused.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) &&
                patternObject is TextPattern textPattern)
            {
                return ReadTypingSegmentViaTextPattern(textPattern, focused);
            }

            return ReadTypingSegmentViaValuePattern(focused);
        }
        catch (ElementNotAvailableException)
        {
            return TypingSegmentFailure("输入控件已变化。");
        }
        catch (InvalidOperationException)
        {
            return TypingSegmentFailure("当前应用无法安全读取输入文字。");
        }
        catch (COMException)
        {
            return TypingSegmentFailure("无法安全读取当前输入文字。");
        }
        catch (ArgumentException)
        {
            return TypingSegmentFailure("当前文本控件返回了无效范围。");
        }
        catch (NotSupportedException)
        {
            return TypingSegmentFailure("当前文本控件不支持安全读取。");
        }
    }

    private static TypingSegmentReadResult ReadTypingSegmentViaTextPattern(TextPattern textPattern, AutomationElement focused)
    {
        var selections = textPattern.GetSelection();
        if (selections.Length != 1)
        {
            return TypingSegmentFailure("输入位置无法确认。");
        }

        var selection = selections[0];
        var inputBounds = focused.Current.BoundingRectangle;
        var bounds = inputBounds.IsEmpty ? (Rect?)null : inputBounds;
        var foreground = GetForegroundWindow();

        if (selection.CompareEndpoints(
                TextPatternRangeEndpoint.Start,
                selection,
                TextPatternRangeEndpoint.End) != 0)
        {
            // A manual selection IS the round: the injection types over it, so no
            // backspaces are needed and the exact content is already known.
            var selected = selection.GetText(TypingSegmentReadCharacters)?.Trim() ?? string.Empty;
            if (selected.Length == 0)
            {
                return TypingSegmentFailure("选中内容为空。");
            }

            return new TypingSegmentReadResult(true, false, true, 0, false, string.Empty, selected,
                ReadPrecedingCharacter(textPattern, selection), bounds, foreground, null);
        }

        // Tail: the last characters before the caret.
        var tailRange = textPattern.DocumentRange.Clone();
        tailRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
        tailRange.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -TypingSegmentReadCharacters);
        var tail = tailRange.GetText(TypingSegmentReadCharacters) ?? string.Empty;

        // Length before the caret (capped; the caller falls back to a sentence scan
        // for very large documents where the probe hits the cap).
        var prefixRange = textPattern.DocumentRange.Clone();
        prefixRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
        var prefix = prefixRange.GetText(TypingSegmentLengthProbe) ?? string.Empty;

        return new TypingSegmentReadResult(true, false, false, prefix.Length,
            prefix.Length >= TypingSegmentLengthProbe, tail, null, null, bounds, foreground, null);
    }

    private static TypingSegmentReadResult ReadTypingSegmentViaValuePattern(AutomationElement focused)
    {
        if (!focused.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) ||
            valueObject is not ValuePattern valuePattern)
        {
            return TypingSegmentFailure("当前应用未提供可读取的输入文字。");
        }

        var value = valuePattern.Current.Value;
        if (value is null)
        {
            return TypingSegmentFailure("当前应用未提供可读取的输入文字。");
        }

        var tail = value.Length > TypingSegmentReadCharacters ? value[^TypingSegmentReadCharacters..] : value;
        var bounds = focused.Current.BoundingRectangle;
        return new TypingSegmentReadResult(true, false, false, value.Length, false, tail, null, null,
            bounds.IsEmpty ? null : bounds, GetForegroundWindow(), null);
    }

    private static char? ReadPrecedingCharacter(TextPattern textPattern, TextPatternRange selection)
    {
        try
        {
            var range = textPattern.DocumentRange.Clone();
            range.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.Start);
            if (range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1) == 0)
            {
                return null;
            }

            var text = range.GetText(1);
            return text.Length > 0 ? text[0] : null;
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static TypingSegmentReadResult TypingSegmentFailure(string message) =>
        new(false, false, false, 0, false, string.Empty, null, null, null, GetForegroundWindow(), message);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
