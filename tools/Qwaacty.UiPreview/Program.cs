using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Qwaacty.App;
using Qwaacty.Core;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var outputDirectory = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal))
            ?? Path.Combine(AppContext.BaseDirectory, "preview");
        Directory.CreateDirectory(outputDirectory);

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // LookupWindow.xaml uses app-level styles (VsButton, VsCheckBox…); a bare
        // Application has no resources, so merge the same theme dictionary App.xaml uses.
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Qwaacty.App;component/Themes/VSCodeTheme.xaml", UriKind.Absolute)
        });

        if (args.Contains("--pro-stream"))
        {
            RenderProfessionalStream(outputDirectory);
            application.Shutdown();
            return;
        }

        Render("popup-multi.png", BuildMultiEntrySample(), outputDirectory, 14);
        Render("popup-single.png", BuildSingleEntrySample(), outputDirectory, 14);
        Render("popup-long.png", BuildLongSample(), outputDirectory, 24);
        Render("popup-ai-entries.png", BuildMultiEntrySample(), outputDirectory, 14,
            aiTitle: "AI 增强翻译", aiText: "语境义：v. 希望；期望 / 情境翻译：I hope so.");
        Render("popup-cn-reverse.png", BuildChineseReverseSample(), outputDirectory, 14,
            compactTyping: true, typingSourceQuery: "覆盖",
            aiTitle: "AI 增强翻译", aiText: "语境义：v. 覆盖；涵盖 / 情境翻译：cover / include");

        application.Shutdown();
    }

    private static void Render(
        string fileName,
        IReadOnlyList<DictionaryEntry> entries,
        string outputDirectory,
        double fontSize,
        bool compactTyping = false,
        string? typingSourceQuery = null,
        string? aiTitle = null,
        string? aiText = null)
    {
        var window = new LookupWindow();
        window.ApplyAppearance("Segoe UI", fontSize, 0.95, "#F21E1E1E", "#FFD4D4D4");
        window.ApplyLayout(compactTyping ? 760 : 340, compactTyping ? 144 : 440);
        if (compactTyping)
        {
            window.ShowEntries(
                entries,
                compactTyping: true,
                typingAnchor: new Rect(100, 100, 2, 16),
                typingInputBounds: new Rect(80, 90, 320, 40),
                typingSourceQuery: typingSourceQuery);
        }
        else
        {
            window.ShowEntries(entries);
        }

        if (aiText is not null)
        {
            window.SetAiSection(aiTitle ?? "AI 增强翻译", aiText);
        }

        Pump();

        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        Pump();

        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var target = new RenderTargetBitmap(Math.Max(width, 1), Math.Max(height, 1), 96, 96, PixelFormats.Pbgra32);
        target.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        var path = Path.Combine(outputDirectory, fileName);
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        Console.WriteLine($"{fileName}: {width}x{height} (window W={window.Width} AW={window.ActualWidth} SC={window.SizeToContent})");
        window.Hide();
        window.Close();
        Pump();
    }

    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    /// <summary>Runs a nested dispatcher frame for the given time so DispatcherTimers
    /// (the typewriter) and layout passes keep firing while the caller waits.</summary>
    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(milliseconds),
            DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void SaveSnapshot(FrameworkElement root, string fileName, string outputDirectory)
    {
        root.UpdateLayout();
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        if (width <= 0 || height <= 0)
        {
            Console.WriteLine($"{fileName}: skipped (no layout)");
            return;
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using (var stream = File.Create(Path.Combine(outputDirectory, fileName)))
        {
            encoder.Save(stream);
        }

        Console.WriteLine($"{fileName}: {width}x{height}");
    }

    /// <summary>Simulates the professional AI enhancement stream (chunks arriving faster
    /// than the steady reveal) and records how the popup height grows over time, so the
    /// smooth-streaming behaviour can be verified without a live AI service.</summary>
    private static void RenderProfessionalStream(string outputDirectory)
    {
        var window = new LookupWindow();
        window.ApplyAppearance("Segoe UI", 14, 0.95, "#F21E1E1E", "#FFD4D4D4");
        window.ApplyLayout(340, 440);
        var entry = new ProfessionalEntry(
            "Impedance Matching",
            "阻抗匹配",
            false,
            "使信号源与负载的阻抗互为共轭，以实现最大功率传输并减小反射的技术。",
            "https://baike.baidu.com/item/Impedance%20Matching");
        window.ShowProfessional(entry);
        Pump();
        window.Hide();

        var root = (FrameworkElement)window.Content;
        const string full = "【翻译】中英对照：英文术语“Impedance Matching”；常见中文译名：“阻抗匹配”。若为缩略语请给出英文全称与标准中文译名。\n【解释】阻抗匹配指使信号源与负载的阻抗互为共轭，从而在传输线上实现最大功率传输并最小化信号反射。关键参数包括反射系数、驻波比（VSWR）与回波损耗；工程上常用 L 型、π 型与 T 型网络以及传输线变压器实现，广泛应用于射频功率放大器、天线馈线与高速数字链路。";

        Console.WriteLine($"full length={full.Length} chars; reveal at 20 chars/s ≈ {full.Length / 20.0:F1}s");
        var clock = Stopwatch.StartNew();
        var fed = 0;
        var nextFeedMs = 0;
        var nextSampleMs = 200;
        var nextSnapshot = 0;
        var snapshots = new[] { 1000, 3000, 6000, 9000 };
        var heights = new List<(int Ms, double Height)>();
        while (clock.ElapsedMilliseconds < 13000)
        {
            var ms = (int)clock.ElapsedMilliseconds;
            if (ms >= nextFeedMs && fed < full.Length)
            {
                fed = Math.Min(full.Length, fed + 12);
                window.ShowAiEnhanceText(full[..fed]);
                nextFeedMs = ms + 120;
            }

            Wait(100);

            ms = (int)clock.ElapsedMilliseconds;
            if (ms >= nextSampleMs)
            {
                heights.Add((ms, root.ActualHeight));
                nextSampleMs = ms + 200;
            }

            if (nextSnapshot < snapshots.Length && ms >= snapshots[nextSnapshot])
            {
                SaveSnapshot(root, $"pro-stream-{snapshots[nextSnapshot]}ms.png", outputDirectory);
                nextSnapshot++;
            }

            if (fed >= full.Length && ms >= 11000)
            {
                break;
            }
        }

        window.CompleteAiEnhance();
        Wait(300);
        SaveSnapshot(root, "pro-stream-final.png", outputDirectory);
        window.Close();

        var maxJump = 0.0;
        for (var index = 1; index < heights.Count; index++)
        {
            maxJump = Math.Max(maxJump, Math.Abs(heights[index].Height - heights[index - 1].Height));
        }

        Console.WriteLine("height samples: " + string.Join(" ", heights.Select(item => $"{item.Ms}ms:{item.Height:F0}")));
        Console.WriteLine(heights.Count > 0
            ? $"samples={heights.Count} first={heights[0].Height:F0} last={heights[^1].Height:F0} maxJump/200ms={maxJump:F1}px"
            : "no samples");
    }

    private static List<DictionaryEntry> BuildMultiEntrySample() =>
    [
        new("hope", LookupDirection.EnglishToChinese,
            ["n. 希望；期望；信心vi. 希望；期望；期待；信赖vt. 希望；期望", "n.&v.希望", "希望；期望；信心"],
            "本地词库", null, "美 [hoʊp]  英 [həʊp]", true),
        new("wish", LookupDirection.EnglishToChinese,
            ["n. 希望；祝福；心愿vt. 祝愿；渴望；向…致问候语vi. 愿望；需要"],
            "本地词库", null, "美 [wɪʃ]  英 [wɪʃ]", true),
        new("want", LookupDirection.EnglishToChinese,
            ["n. 需要；缺乏；贫困；必需品vt. 希望；应该；缺少"],
            "本地词库", null, "美 [wɑːnt]  英 [wɒnt]", true),
        new("like", LookupDirection.EnglishToChinese,
            ["vt. 喜欢；想；愿意vi. 喜欢；希望prep. 像；如同"],
            "本地词库", null, "美 [laɪk]  英 [laɪk]", true),
        new("promise", LookupDirection.EnglishToChinese,
            ["n. 许诺，允诺；希望vt. 允诺，许诺；给人以…的指望或希望"],
            "本地词库", null, "美 [ˈprɑːmɪs]  英 [ˈprɒmɪs]", true)
    ];

    private static List<DictionaryEntry> BuildSingleEntrySample() =>
    [
        new("cancel", LookupDirection.EnglishToChinese,
            ["取消， 撤销； 删去", "vi. 取消，撤销,vt. 取消；删去,vi. 取消；相互抵消,n. 取消，撤销", "vi. 取消，撤销", "vt. 取消；删去"],
            "本地词库", null, "美 ['kænsl]  英 ['kænsl]", true)
    ];

    private static List<DictionaryEntry> BuildLongSample()
    {
        var entries = new List<DictionaryEntry>
        {
            new("govern", LookupDirection.EnglishToChinese,
                [
                    "vt. 管理；支配；统治；控制,vi. 居支配地位；进行统治",
                    "n. 政府；治理；统治；控制；管理方法",
                    "adj. 政府的；治理的；控制的",
                    "adv. 治理地；控制地；管理地"
                ],
                "本地词库", null, "美 ['ɡʌvɚn]  英 ['ɡʌvn]", true)
        };

        string[] extras = ["government", "governor", "governess", "governance", "governmental", "governing", "governed", "governs", "governable", "governorate"];
        foreach (var extra in extras)
        {
            entries.Add(new DictionaryEntry(extra, LookupDirection.EnglishToChinese,
                ["n. 治理；管理；统辖"], "本地词库", null, "美 [ˈɡʌvərn]  英 [ˈɡʌvən]", true));
        }

        return entries;
    }

    private static List<DictionaryEntry> BuildChineseReverseSample() =>
    [
        new("cover", LookupDirection.ChineseToEnglish,
            ["vt. 覆盖；遮盖；涉及；包括；足以支付", "n. 覆盖物；盖子；封面"],
            "本地词库", null, "美 [ˈkʌvər]  英 [ˈkʌvə]", true),
        new("overlay", LookupDirection.ChineseToEnglish,
            ["n. 覆盖物；覆盖层；图层", "vt. 覆盖；铺在…上面"],
            "本地词库", null, "美 [ˈoʊvərleɪ]", true),
        new("mantle", LookupDirection.ChineseToEnglish,
            ["n. 覆盖物；斗篷；地幔", "vt. 覆盖；遮掩"],
            "本地词库", null, "美 [ˈmæntl]", true),
        new("sheathe", LookupDirection.ChineseToEnglish,
            ["vt. 覆盖；包装；插入鞘"],
            "本地词库", null, null, true),
        new("wreathe", LookupDirection.ChineseToEnglish,
            ["vt. 覆盖；环绕；盘绕"],
            "本地词库", null, null, true)
    ];
}
