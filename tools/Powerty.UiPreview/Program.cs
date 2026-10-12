using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Powerty.App;
using Powerty.Core;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var outputDirectory = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "preview");
        Directory.CreateDirectory(outputDirectory);

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

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
