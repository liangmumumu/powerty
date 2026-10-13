using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Qwaacty.App;

internal sealed record OcrCaptureResult(bool Ok, string? Text, string? Message);

/// <summary>Captures a screen region and recognizes its text with the built-in Windows OCR engine.</summary>
internal static class ScreenOcr
{
    public static async Task<OcrCaptureResult> CaptureAsync(int x, int y, int width, int height)
    {
        if (width < 8 || height < 8)
        {
            return new OcrCaptureResult(false, null, "选取的区域太小。");
        }

        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            }

            var scaled = Upscale(bitmap);
            try
            {
                return await RecognizeAsync(scaled ?? bitmap);
            }
            finally
            {
                scaled?.Dispose();
            }
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or IOException or InvalidOperationException or ArgumentException)
        {
            return new OcrCaptureResult(false, null, $"截图识别失败：{exception.Message}");
        }
    }

    private static async Task<OcrCaptureResult> RecognizeAsync(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var bytes = stream.ToArray();

        var randomAccessStream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(randomAccessStream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        writer.DetachStream();
        randomAccessStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

        var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans"))
                     ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            return new OcrCaptureResult(false, null,
                "系统未安装 OCR 语言包：请在「设置 → 时间和语言 → 语言和区域」中为中文/英文安装“光学字符识别”功能。");
        }

        var result = await engine.RecognizeAsync(softwareBitmap);
        return string.IsNullOrWhiteSpace(result.Text)
            ? new OcrCaptureResult(false, null, "未识别到文字（可尝试框选得更紧凑，或放大目标文字后重试）。")
            : new OcrCaptureResult(true, result.Text, null);
    }

    /// <summary>Upscales small captures: OCR accuracy improves noticeably with larger glyphs.</summary>
    private static Bitmap? Upscale(Bitmap source)
    {
        var longestSide = Math.Max(source.Width, source.Height);
        if (longestSide >= 900 || longestSide == 0)
        {
            return null;
        }

        var factor = Math.Clamp(900.0 / longestSide, 1.5, 3.0);
        var width = (int)Math.Round(source.Width * factor);
        var height = (int)Math.Round(source.Height * factor);
        if (width > 8000 || height > 8000)
        {
            return null;
        }

        var scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, width, height);
        return scaled;
    }
}
