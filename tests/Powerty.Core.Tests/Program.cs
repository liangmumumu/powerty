using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Powerty.Core;

if (args.Length > 1 && args[0] == "--stress")
{
    RunStress(args[1]);
    return;
}

if (args.Length > 1 && args[0] == "--probe")
{
    RunProbe(args[1], args.Length > 2 ? args[2] : null);
    return;
}

if (args.Length > 2 && args[0] == "--dump")
{
    RunDump(args[1], args[2]);
    return;
}

var passed = 0;
RunSync("English current word and bounded phrase", () =>
{
    var candidates = InputContextParser.Parse("I like good morning", 19);
    Assert(candidates.Count == 2, "Expected a word and phrase.");
    Assert(candidates[0] == new QueryCandidate("morning", LookupDirection.EnglishToChinese), "Wrong current word.");
    Assert(candidates[1] == new QueryCandidate("like good morning", LookupDirection.EnglishToChinese), "Wrong phrase.");
});

RunSync("Chinese query around the caret", () =>
{
    var candidates = InputContextParser.Parse("测试中文词条", 6);
    Assert(candidates.SequenceEqual([
        new QueryCandidate("中文词条", LookupDirection.ChineseToEnglish)
    ]), "Wrong Chinese query.");
});

RunSync("Mixed-language typing follows the character before the caret", () =>
{
    var englishCandidates = InputContextParser.Parse("hello中文", 5);
    Assert(englishCandidates[0] == new QueryCandidate("hello", LookupDirection.EnglishToChinese),
        "English input before the caret should be preferred.");

    var chineseCandidates = InputContextParser.Parse("hello中文", 7);
    Assert(chineseCandidates[0] == new QueryCandidate("中文", LookupDirection.ChineseToEnglish),
        "Chinese input before the caret should be preferred.");
});

RunSync("Punctuation does not enter an English phrase", () =>
{
    var candidates = InputContextParser.Parse("read this. hello", 16);
    Assert(candidates.Any(candidate => candidate.Text == "hello"), "Expected the current word.");
    Assert(candidates.All(candidate => !candidate.Text.Contains('.')), "Punctuation leaked into the query.");
});

RunAsync("Local hits do not reach the online provider", async () =>
{
    var online = new RecordingOnlineProvider();
    var lookup = new DictionaryLookup(new LocalDictionaryProvider(), online);
    var response = await lookup.FindAsync("hello", LookupDirection.EnglishToChinese, allowOnline: true, CancellationToken.None);
    Assert(response.Found && !response.IsOnline, "Expected a local hit.");
    Assert(online.Queries.Count == 0, "Online lookup was called after a local hit.");
});

RunAsync("Online provider receives only the selected query", async () =>
{
    var online = new RecordingOnlineProvider();
    var lookup = new DictionaryLookup(new EmptyLocalProvider(), online);
    const string query = "good morning";
    var response = await lookup.FindAsync(query, LookupDirection.EnglishToChinese, allowOnline: true, CancellationToken.None);
    Assert(response.IsOnline && response.Found, "Expected an online fallback result.");
    Assert(online.Queries.SequenceEqual([query]), "Unexpected query text was sent to the online provider.");
});

RunAsync("Online fallback can be disabled", async () =>
{
    var online = new RecordingOnlineProvider();
    var lookup = new DictionaryLookup(new EmptyLocalProvider(), online);
    var response = await lookup.FindAsync("unlisted", LookupDirection.EnglishToChinese, allowOnline: false, CancellationToken.None);
    Assert(!response.Found && online.Queries.Count == 0, "Online fallback was not disabled.");
});

RunSync("Gloss tokenizer extracts CJK glosses", () =>
{
    var tokens = GlossTokenizer.Split("vi. 取消失败,vt. 取消；删除 (计算机)");
    Assert(tokens.SequenceEqual(["取消失败", "取消", "删除"]), $"Unexpected tokens: {string.Join("|", tokens)}");
    Assert(GlossTokenizer.DedupKey("取消， 撤销 (vt.)") == "取消撤销", "Dedup key mismatch.");
});

RunSync("Lexicon round-trip and forward lookup", () =>
{
    var provider = BuildProvider(
        new LexiconWord("cancel", "cancel", ["取消， 撤销； 删去", "取消 (vt.)"], "'kænsl", "ˈkænsl"),
        new LexiconWord("good morning", "good morning", ["早上好"], "", "ˌɡʊd ˈmɔːnɪŋ"),
        new LexiconWord("call off", "call off", ["取消"], "", ""));
    Assert(provider.WordCount == 3, "Wrong word count.");

    var result = provider.FindAsync("Cancel", LookupDirection.EnglishToChinese, CancellationToken.None).GetAwaiter().GetResult();
    Assert(result.Count == 1, "Expected one entry.");
    Assert(result[0].Query == "cancel", "Lookup was not case-insensitive.");
    Assert(result[0].Definitions.Count == 2, "Definition count changed.");
    Assert(!string.IsNullOrEmpty(result[0].Phonetic), "Phonetic missing.");

    var phrase = provider.FindAsync("good morning", LookupDirection.EnglishToChinese, CancellationToken.None).GetAwaiter().GetResult();
    Assert(phrase.Count == 1, "Phrase lookup failed.");
});

RunSync("Lexicon reverse lookup prefers plain words", () =>
{
    var provider = BuildProvider(
        new LexiconWord("cancel", "cancel", ["取消"], "", ""),
        new LexiconWord("call off", "call off", ["取消"], "", ""),
        new LexiconWord("谢谢", "谢谢", ["thanks"], "", ""));
    var result = provider.FindAsync("取消", LookupDirection.ChineseToEnglish, CancellationToken.None).GetAwaiter().GetResult();
    Assert(result.Count == 2, $"Expected two suggestions, got {result.Count}.");
    Assert(result[0].Query == "cancel", "Single word should be suggested first.");
    Assert(result.All(entry => entry.Direction == LookupDirection.ChineseToEnglish), "Wrong direction.");
});

RunSync("Lexicon rejects invalid files", () =>
{
    var temporary = Path.Combine(Path.GetTempPath(), $"tt-lexicon-{Guid.NewGuid():N}.ttlx");
    try
    {
        File.WriteAllBytes(temporary, [1, 2, 3, 4]);
        Assert(!LexiconDictionaryProvider.TryLoad(temporary, out _, out var error) && error is not null,
            "Corrupt lexicon was accepted.");
        Assert(!LexiconDictionaryProvider.TryLoad(temporary + ".missing", out _, out _),
            "Missing lexicon was accepted.");
    }
    finally
    {
        File.Delete(temporary);
    }
});

RunSync("Portable mode redirects settings next to the executable", () =>
{
    var temporary = Path.Combine(Path.GetTempPath(), $"tt-portable-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temporary);
    try
    {
        var fallback = Path.Combine(temporary, "fallback");
        Assert(!PortableMode.IsPortable(temporary), "Marker should be absent.");
        Assert(PortableMode.ResolveSettingsPath(temporary, fallback) == Path.Combine(fallback, "settings.json"),
            "Fallback settings path expected.");

        File.WriteAllText(Path.Combine(temporary, PortableMode.MarkerFileName), string.Empty);
        Assert(PortableMode.IsPortable(temporary), "Marker should be detected.");
        Assert(PortableMode.ResolveSettingsPath(temporary, fallback) == Path.Combine(temporary, "settings.json"),
            "Portable settings path expected.");
    }
    finally
    {
        Directory.Delete(temporary, recursive: true);
    }
});

RunSync("Parser handles carets inside words and special characters", () =>
{
    Assert(InputContextParser.Parse("hello", 3).Any(candidate => candidate.Text == "hello"),
        "Caret inside a word should query the whole word.");
    Assert(InputContextParser.Parse("don't stop", 5).Any(candidate => candidate.Text == "don't"),
        "Apostrophe should stay inside the word.");
    Assert(InputContextParser.Parse("well-known fact", 10).Any(candidate => candidate.Text == "well-known"),
        "Hyphen should stay inside the word.");
    Assert(InputContextParser.Parse("hello ", 6).Any(candidate => candidate.Text == "hello"),
        "A trailing space should still find the previous word.");
});

RunSync("Parser bounds and mixed content", () =>
{
    Assert(InputContextParser.Parse("", 0).Count == 0, "Empty context should produce no candidates.");
    Assert(InputContextParser.Parse("abc", -1).Count == 0, "Negative caret should produce no candidates.");
    Assert(InputContextParser.Parse("abc", 99).Count == 0, "Caret past the end should produce no candidates.");
    Assert(InputContextParser.Parse(",,,。", 2).Count == 0, "Punctuation-only text should produce no candidates.");
    Assert(InputContextParser.Parse(new string('a', 60), 60).Count == 0, "Over-long words should be skipped.");

    var mixed = InputContextParser.Parse("abc中文", 5);
    Assert(mixed.SequenceEqual([new QueryCandidate("中文", LookupDirection.ChineseToEnglish)]),
        "Mixed text should yield the Chinese fragment.");

    var chinese = InputContextParser.Parse("四个汉字测试", 6);
    Assert(chinese.Count == 1 && chinese[0].Text == "汉字测试",
        $"Chinese queries should be capped at four characters, got: {string.Join(',', chinese.Select(candidate => candidate.Text))}");
});

RunSync("Lookup trims queries, rejects over-long input and ignores blanks", () =>
{
    var online = new RecordingOnlineProvider();
    var lookup = new DictionaryLookup(new LocalDictionaryProvider(), online);

    var padded = lookup.FindAsync("  hello  ", LookupDirection.EnglishToChinese, allowOnline: true, CancellationToken.None).GetAwaiter().GetResult();
    Assert(padded.Found, "A padded query should be trimmed and found.");

    var tooLong = lookup.FindAsync(new string('a', 49), LookupDirection.EnglishToChinese, allowOnline: true, CancellationToken.None).GetAwaiter().GetResult();
    Assert(!tooLong.Found && tooLong.Error is not null, "Over-long queries should be rejected with a message.");

    var blank = lookup.FindAsync("   ", LookupDirection.EnglishToChinese, allowOnline: true, CancellationToken.None).GetAwaiter().GetResult();
    Assert(!blank.Found && blank.Error is null, "Blank queries should return quietly.");

    Assert(online.Queries.Count == 0, "Rejected queries must not reach the online provider.");
});

RunAsync("Online failures map to readable messages", async () =>
{
    var cases = new (Exception Error, string Expected)[]
    {
        (new HttpRequestException("offline"), "在线查词暂不可用"),
        (new TaskCanceledException("timeout"), "在线查词超时"),
        (new JsonException("bad payload"), "无法识别"),
        (new IOException("broken pipe"), "读取在线查词结果失败")
    };

    foreach (var (error, expected) in cases)
    {
        var lookup = new DictionaryLookup(new EmptyLocalProvider(), new ThrowingOnlineProvider(error));
        var response = await lookup.FindAsync("unlisted", LookupDirection.EnglishToChinese, allowOnline: true, CancellationToken.None);
        Assert(!response.Found && response.Error is not null && response.Error.Contains(expected),
            $"Unexpected message for {error.GetType().Name}: {response.Error}");
    }
});

RunAsync("Cancellation propagates instead of being swallowed", async () =>
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var lookup = new DictionaryLookup(new EmptyLocalProvider(), new ThrowingOnlineProvider(new OperationCanceledException()));
    try
    {
        await lookup.FindAsync("unlisted", LookupDirection.EnglishToChinese, allowOnline: true, cancellation.Token);
        throw new InvalidOperationException("Expected OperationCanceledException.");
    }
    catch (OperationCanceledException)
    {
    }
});

RunAsync("MyMemory provider parses responses and enforces the rate limit", async () =>
{
    var responses = new Queue<string>([
        "{\"responseData\":{\"translatedText\":\"你好\"}}",
        "{\"responseData\":{\"translatedText\":\"hello\"}}"
    ]);
    var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json")
    });
    using var client = new HttpClient(handler);
    var provider = new MyMemoryLookupProvider(client);

    var first = await provider.FindAsync("hello", LookupDirection.EnglishToChinese, CancellationToken.None);
    Assert(first.Count == 1 && first[0].Definitions[0] == "你好" && first[0].Source.Contains("MyMemory"),
        "Unexpected parse result from the stubbed response.");

    var rateWatch = Stopwatch.StartNew();
    var second = await provider.FindAsync("hello", LookupDirection.EnglishToChinese, CancellationToken.None);
    rateWatch.Stop();
    Assert(second.Count == 0, "An echoed translation should be discarded.");
    Assert(rateWatch.ElapsedMilliseconds >= 900, $"Rate limit not applied: {rateWatch.ElapsedMilliseconds} ms.");

    var tooLong = await provider.FindAsync(new string('b', 121), LookupDirection.EnglishToChinese, CancellationToken.None);
    Assert(tooLong.Count == 0, "Over-long queries should be rejected locally.");
    Assert(handler.RequestUris.Count == 2, $"Unexpected request count: {handler.RequestUris.Count}.");

    var requestUri = Uri.UnescapeDataString(handler.RequestUris[0]);
    Assert(requestUri.Contains("q=hello") && requestUri.Contains("langpair=en|zh-CN"),
        $"Unexpected request: {requestUri}");
});

RunAsync("MyMemory provider surfaces HTTP errors", async () =>
{
    var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
    {
        Content = new StringContent("oops")
    });
    using var client = new HttpClient(handler);
    var provider = new MyMemoryLookupProvider(client);
    try
    {
        await provider.FindAsync("hello", LookupDirection.EnglishToChinese, CancellationToken.None);
        throw new InvalidOperationException("Expected HttpRequestException.");
    }
    catch (HttpRequestException)
    {
    }
});

RunSync("MyMemory quota rejection message explains the wait", () =>
{
    var message = MyMemoryLookupProvider.DescribeQuotaLimit(
        "{\"responseDetails\":\"MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS FOR TODAY. NEXT AVAILABLE IN  16 HOURS 46 MINUTES 42 SECONDS\"}");
    Assert(message.Contains("额度已用完") && message.Contains("16 小时 46 分"),
        $"Unexpected quota message: {message}");
    Assert(MyMemoryLookupProvider.DescribeQuotaLimit(null).Contains("额度已用完"),
        "The generic quota message should still explain the quota.");
});

RunAsync("Quota exhaustion surfaces as a friendly error instead of a transport failure", async () =>
{
    var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
    {
        Content = new StringContent(
            "{\"responseDetails\":\"NEXT AVAILABLE IN  2 HOURS 0 MINUTES 1 SECONDS\"}",
            Encoding.UTF8,
            "application/json")
    });
    using var client = new HttpClient(handler);
    var provider = new MyMemoryLookupProvider(client);
    var lookup = new DictionaryLookup(new EmptyLocalProvider(), provider);
    var response = await lookup.FindAsync("unknownword", LookupDirection.EnglishToChinese, allowOnline: true, CancellationToken.None);
    Assert(!response.Found && response.Error is not null && response.Error.Contains("额度已用完"),
        $"Unexpected quota response: {response.Error}");
});

RunSync("Lexicon format rejects corrupted payloads", () =>
{
    using var stream = new MemoryStream();
    LexiconFormat.Save(stream, [new LexiconWord("cancel", "cancel", ["取消"], "", "")]);
    var valid = stream.ToArray();

    Assert(LoadFails(valid[..3]), "A truncated header should be rejected.");
    Assert(LoadFails([1, 2, 3, 4, 5]), "A wrong magic number should be rejected.");

    var wrongVersion = (byte[])valid.Clone();
    wrongVersion[4] = 99;
    Assert(LoadFails(wrongVersion), "An unsupported version should be rejected.");
    Assert(LoadFails(valid[..^5]), "A truncated body should be rejected.");

    var negativeCount = new byte[] { 0x54, 0x54, 0x4C, 0x58, 1, 0xFF, 0xFF, 0xFF, 0xFF };
    Assert(LoadFails(negativeCount), "A negative entry count should be rejected.");

    var garbage = new byte[256];
    new Random(42).NextBytes(garbage);
    Assert(LoadFails(garbage), "Random garbage should be rejected.");
});

RunSync("Reverse suggestions are ranked and capped", () =>
{
    var provider = BuildProvider(
        new LexiconWord("call off", "call off", ["取消"], "", ""),
        new LexiconWord("cancel", "cancel", ["取消"], "", ""),
        new LexiconWord("abort", "abort", ["取消"], "", ""),
        new LexiconWord("annul", "annul", ["取消"], "", ""),
        new LexiconWord("scrap", "scrap", ["取消"], "", ""),
        new LexiconWord("revoke", "revoke", ["取消"], "", ""),
        new LexiconWord("rescind", "rescind", ["取消"], "", ""));

    var result = provider.FindAsync("取消", LookupDirection.ChineseToEnglish, CancellationToken.None).GetAwaiter().GetResult();
    Assert(result.Count == 5, $"Expected five suggestions, got {result.Count}.");
    Assert(result[0].Query == "abort", $"The shortest plain word should rank first, got {result[0].Query}.");
    Assert(result.Any(entry => entry.Query == "cancel") &&
           result.All(entry => entry.Query != "call off" && entry.Query != "rescind"),
        $"Unexpected cap set: {string.Join(", ", result.Select(entry => entry.Query))}");
});

RunSync("Reverse suggestions prefer common words over short obscure ones", () =>
{
    var provider = BuildProvider(
        new LexiconWord("cap", "cap", ["覆盖"], "", "", 2500),
        new LexiconWord("cake", "cake", ["覆盖"], "", "", 4200),
        new LexiconWord("robe", "robe", ["覆盖"], "", "", 8000),
        new LexiconWord("cover", "cover", ["覆盖"], "", "", 380),
        new LexiconWord("pall", "pall", ["覆盖"], "", "", 0),
        new LexiconWord("hood", "hood", ["覆盖"], "", "", 0));

    var result = provider.FindAsync("覆盖", LookupDirection.ChineseToEnglish, CancellationToken.None).GetAwaiter().GetResult();
    Assert(result.Count == 5, $"Expected five suggestions, got {result.Count}.");
    Assert(result[0].Query == "cover", $"The most common word should rank first, got {result[0].Query}.");
    Assert(result[1].Query == "cap", $"Second should be the next most common, got {result[1].Query}.");
    Assert(result[2].Query == "cake" && result[3].Query == "robe",
        $"Known-frequency words should outrank unknown ones: {string.Join(", ", result.Select(entry => entry.Query))}");
    Assert(result[4].Query == "hood", $"Unknown-frequency words fall back to length order, got {result[4].Query}.");
});

RunSync("Gloss tokenizer ignores non-gloss content", () =>
{
    Assert(GlossTokenizer.Split("vt. to cancel something").Count == 0, "English-only text should yield no glosses.");
    Assert(GlossTokenizer.DedupKey("hello world") == "", "The dedup key of pure Latin text should be empty.");
    var tokens = GlossTokenizer.Split("取消（计算机）, 删除; 取消");
    Assert(tokens.SequenceEqual(["取消", "删除"]), $"Unexpected tokens: {string.Join('|', tokens)}");
});

RunSync("Typing buffer keeps a bounded rolling window", () =>
{
    var buffer = new TypingBuffer(16);
    foreach (var character in "hello world")
    {
        buffer.Append(character);
    }

    Assert(buffer.Snapshot() == "hello world", $"Unexpected snapshot: {buffer.Snapshot()}");
    buffer.Backspace();
    buffer.Backspace();
    Assert(buffer.Snapshot() == "hello wor", "Backspace should remove the last character.");

    for (var index = 0; index < 40; index++)
    {
        buffer.Append((char)('a' + (index % 26)));
    }

    Assert(buffer.Length == 16, $"Buffer should cap at 16, got {buffer.Length}.");
    buffer.Clear();
    Assert(buffer.Length == 0, "Clear should empty the buffer.");
    buffer.Append('\n');
    Assert(buffer.Length == 0, "Control characters should be ignored.");
});

RunSync("Definition formatter groups glosses by part of speech", () =>
{
    var groups = DefinitionFormatter.Format([
        "n. 希望；期望；信心vi. 希望；期望；期待；信赖vt. 希望；期望",
        "n.&v.希望",
        "希望；期望；信心"
    ]);

    Assert(groups.Count == 3, $"Expected three groups, got {groups.Count}: {string.Join(" | ", groups.Select(group => group.PartOfSpeech))}");
    Assert(groups[0].PartOfSpeech == "n." && groups[0].Fragments.SequenceEqual(["希望", "期望", "信心"]),
        $"Unexpected first group: {groups[0].PartOfSpeech} / {string.Join(",", groups[0].Fragments)}");
    Assert(groups[1].PartOfSpeech == "vi." && groups[1].Fragments.SequenceEqual(["希望", "期望", "期待", "信赖"]),
        $"Unexpected second group: {groups[1].PartOfSpeech} / {string.Join(",", groups[1].Fragments)}");
    Assert(groups[2].PartOfSpeech == "vt." && groups[2].Fragments.SequenceEqual(["希望", "期望"]),
        $"Unexpected third group: {groups[2].PartOfSpeech} / {string.Join(",", groups[2].Fragments)}");
});

RunSync("Definition formatter caps groups and fragments", () =>
{
    var groups = DefinitionFormatter.Format([
        "n. 名词一；名词二；名词三；名词四；名词五；名词六；名词七",
        "v. 动词一",
        "adj. 形容词一",
        "adv. 副词一"
    ]);

    Assert(groups.Count == 3, $"Expected at most three groups, got {groups.Count}.");
    Assert(groups[0].Fragments.Count == 5, $"Expected at most five fragments, got {groups[0].Fragments.Count}.");
});

RunSync("Morphology derives base forms from inflections", () =>
{
    Assert(EnglishMorphology.Candidates("running").Contains("run"), "running -> run");
    Assert(EnglishMorphology.Candidates("stopped").Contains("stop"), "stopped -> stop");
    Assert(EnglishMorphology.Candidates("walked").Contains("walk"), "walked -> walk");
    Assert(EnglishMorphology.Candidates("liked").Contains("like"), "liked -> like");
    Assert(EnglishMorphology.Candidates("studied").Contains("study"), "studied -> study");
    Assert(EnglishMorphology.Candidates("studies").Contains("study"), "studies -> study");
    Assert(EnglishMorphology.Candidates("boxes").Contains("box"), "boxes -> box");
    Assert(EnglishMorphology.Candidates("goes").Contains("go"), "goes -> go");
    Assert(EnglishMorphology.Candidates("makes").Contains("make"), "makes -> make");
    Assert(EnglishMorphology.Candidates("carrying").Contains("carry"), "carrying -> carry");
    Assert(EnglishMorphology.Candidates("making").Contains("make"), "making -> make");
    Assert(EnglishMorphology.Candidates("dying").Contains("die"), "dying -> die");
    Assert(EnglishMorphology.Candidates("quickly").Contains("quick"), "quickly -> quick");
    Assert(EnglishMorphology.Candidates("happily").Contains("happy"), "happily -> happy");
    Assert(EnglishMorphology.Candidates("happier").Contains("happy"), "happier -> happy");
    Assert(EnglishMorphology.Candidates("biggest").Contains("big"), "biggest -> big");
    Assert(EnglishMorphology.Candidates("fully").Contains("full"), "fully -> full");
    Assert(EnglishMorphology.Candidates("cat's").Contains("cat"), "cat's -> cat");
    Assert(EnglishMorphology.Candidates("hello!").Count == 0, "Punctuation should disqualify morphology.");
    Assert(EnglishMorphology.Candidates("is").Count == 0, "Short words should yield no candidates.");
});

RunSync("Morphology recovers irregular forms and chat abbreviations", () =>
{
    Assert(EnglishMorphology.Candidates("gotten").Contains("get"), "gotten -> get");
    Assert(EnglishMorphology.Candidates("sought").Contains("seek"), "sought -> seek");
    Assert(EnglishMorphology.Candidates("swung").Contains("swing"), "swung -> swing");
    Assert(EnglishMorphology.Candidates("withheld").Contains("withhold"), "withheld -> withhold");
    Assert(EnglishMorphology.Candidates("children").Contains("child"), "children -> child");
    Assert(EnglishMorphology.Candidates("thx").Contains("thanks"), "thx -> thanks");
    Assert(EnglishMorphology.Candidates("btw").Contains("by the way"), "btw -> by the way");
    Assert(EnglishMorphology.Candidates("ty").Contains("thank you"), "ty -> thank you");
    Assert(EnglishMorphology.Candidates("xyzzy").Count == 0, "Unknown words should yield no candidates.");
});

RunSync("Chinese suffix ladder derives shorter queries", () =>
{
    var suffixes = ChineseQueryLadder.Suffixes("上午开会");
    Assert(suffixes.SequenceEqual(["午开会", "开会"]), $"Unexpected ladder: {string.Join("|", suffixes)}");
    Assert(ChineseQueryLadder.Suffixes("开会").Count == 0, "Short queries should not ladder.");
});

RunAsync("Typing lookup falls back to shorter Chinese suffixes but selection does not", async () =>
{
    var provider = BuildProvider(
        new LexiconWord("meeting", "meeting", ["会议；开会"], "", ""));
    var lookup = new DictionaryLookup(provider, new RecordingOnlineProvider());
    var typed = await lookup.FindAsync("上午开会", LookupDirection.ChineseToEnglish, allowOnline: false, CancellationToken.None);
    Assert(typed.Found && typed.Entries[0].Query == "meeting", "Typed ladder lookup failed.");
    var selection = await lookup.FindSelectionAsync("上午开会", LookupDirection.ChineseToEnglish, allowOnline: false, CancellationToken.None);
    Assert(!selection.Found, "Selection lookups should not use the typing ladder.");
});

RunSync("Reverse candidates keep the best words when the cap fills up", () =>
{
    var extraWords = new List<LexiconWord>();
    for (var index = 0; index < 30; index++)
    {
        extraWords.Add(new LexiconWord($"aa phrase {index:D2}", $"aa phrase {index:D2}", ["取消"], "", ""));
    }

    extraWords.Add(new LexiconWord("cancel", "cancel", ["取消"], "", ""));
    var provider = BuildProvider([.. extraWords]);
    var results = provider.FindAsync("取消", LookupDirection.ChineseToEnglish, CancellationToken.None).GetAwaiter().GetResult();
    Assert(results.Any(entry => entry.Query == "cancel"), "The single word should survive the reverse candidate cap.");
});

RunSync("Lookup falls back to base form and trims punctuation", () =>
{
    var provider = BuildProvider(
        new LexiconWord("run", "run", ["跑"], "", ""),
        new LexiconWord("cancel", "cancel", ["取消"], "", ""));
    var lookup = new DictionaryLookup(provider, new RecordingOnlineProvider());

    var running = lookup.FindAsync("running", LookupDirection.EnglishToChinese, allowOnline: false, CancellationToken.None).GetAwaiter().GetResult();
    Assert(running.Found && running.Entries[0].Query == "run", "running should resolve to run offline.");

    var quoted = lookup.FindAsync("\"cancel.\"", LookupDirection.EnglishToChinese, allowOnline: false, CancellationToken.None).GetAwaiter().GetResult();
    Assert(quoted.Found && quoted.Entries[0].Query == "cancel", "Punctuation around the query should be trimmed.");

    var miss = lookup.FindAsync("zzzzzz", LookupDirection.EnglishToChinese, allowOnline: false, CancellationToken.None).GetAwaiter().GetResult();
    Assert(!miss.Found, "Unknown words should stay unfound offline.");
});

RunSync("Translation cache stores, refreshes and evicts", () =>
{
    var cache = new TranslationCache(capacity: 16);
    Assert(!cache.TryGet("hello", LookupDirection.EnglishToChinese, out _), "Empty cache should miss.");
    cache.Set("hello", LookupDirection.EnglishToChinese, "你好");
    Assert(cache.TryGet("hello", LookupDirection.EnglishToChinese, out var text) && text == "你好", "Cache should hit after set.");
    Assert(!cache.TryGet("hello", LookupDirection.ChineseToEnglish, out _), "Directions should not mix.");

    for (var index = 0; index < 40; index++)
    {
        cache.Set($"word{index}", LookupDirection.EnglishToChinese, $"释义{index}");
    }

    Assert(!cache.TryGet("hello", LookupDirection.EnglishToChinese, out _), "Old entries should be evicted.");
    Assert(cache.TryGet("word39", LookupDirection.EnglishToChinese, out var latest) && latest == "释义39", "Newest entry should survive.");
});

RunSync("AI stream parser handles SSE chunks", () =>
{
    Assert(OpenAiCompatibleTranslator.ParseStreamChunk("data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}") == "你好",
        "Standard delta chunk should parse.");
    Assert(OpenAiCompatibleTranslator.ParseStreamChunk("data: [DONE]") is null, "DONE marker should be ignored.");
    Assert(OpenAiCompatibleTranslator.ParseStreamChunk(": keep-alive") is null, "Comments should be ignored.");
    Assert(OpenAiCompatibleTranslator.ParseStreamChunk("data: {broken json") is null, "Invalid JSON should be ignored.");
    Assert(OpenAiCompatibleTranslator.ParseStreamChunk("data: {\"choices\":[]}") is null, "Empty choices should be ignored.");
    Assert(OpenAiCompatibleTranslator.ParseStreamChunk("data: {\"choices\":[{\"delta\":{}}]}") is null, "Chunks without content should be ignored.");
    Assert(OpenAiCompatibleTranslator.ParseStreamChunk("") is null, "Blank lines should be ignored.");
});

RunSync("Context enhance accepts words and phrases only", () =>
{
    Assert(ContextEnhancePrompt.Qualifies("apple"), "Single word should qualify.");
    Assert(ContextEnhancePrompt.Qualifies("in spite of"), "Short phrase should qualify.");
    Assert(ContextEnhancePrompt.Qualifies("as a matter of fact"), "Six-word phrase should qualify.");
    Assert(ContextEnhancePrompt.Qualifies("苹果"), "Short Chinese term should qualify.");
    Assert(ContextEnhancePrompt.Qualifies("一举两得"), "Chinese idiom should qualify.");
    Assert(!ContextEnhancePrompt.Qualifies("This is a complete sentence with many words inside it."),
        "Long sentence should not qualify.");
    Assert(!ContextEnhancePrompt.Qualifies("这是一句很长的中文句子，包含了很多字符，不应该触发增强翻译功能，因为它是句子而不是词。"),
        "Long Chinese sentence should not qualify.");
    Assert(!ContextEnhancePrompt.Qualifies("   "), "Whitespace should not qualify.");
});

RunSync("Context enhance prompt embeds context and target language", () =>
{    var user = ContextEnhancePrompt.BuildUser("bank", "He sat on the river bank and watched the boats.", LookupDirection.EnglishToChinese);
    Assert(user.Contains("river bank", StringComparison.Ordinal), "Context should appear in the user prompt.");
    Assert(user.Contains("bank", StringComparison.Ordinal), "Selection should appear in the user prompt.");
    Assert(user.Contains("翻译为中文", StringComparison.Ordinal), "Target language should be Chinese.");
    Assert(user.Contains("【语境】", StringComparison.Ordinal) && user.Contains("【选中内容】", StringComparison.Ordinal),
        "Prompt should use the labelled sections.");

    var userReverse = ContextEnhancePrompt.BuildUser("银行", null, LookupDirection.ChineseToEnglish);
    Assert(userReverse.Contains("（无可用语境）", StringComparison.Ordinal), "Missing context should be marked.");
    Assert(userReverse.Contains("翻译为英文", StringComparison.Ordinal), "Target language should be English.");

    var system = ContextEnhancePrompt.BuildSystem(LookupDirection.EnglishToChinese);
    Assert(system.Contains("语境义", StringComparison.Ordinal) && system.Contains("情境翻译", StringComparison.Ordinal),
        "System prompt should require the two-line format.");

    var longContext = new string('x', ContextEnhancePrompt.MaxContextCharacters + 200);
    var clipped = ContextEnhancePrompt.BuildUser("word", longContext, LookupDirection.EnglishToChinese);
    Assert(clipped.Length < longContext.Length + 100, "Overlong context should be clipped.");
});

RunSync("OCR text cleaner removes CJK spacing and line breaks", () =>
{
    Assert(OcrTextCleaner.Normalize("Hello world 你 好 世 界") == "Hello world 你好世界",
        $"CJK spaces should be dropped: '{OcrTextCleaner.Normalize("Hello world 你 好 世 界")}'");
    Assert(OcrTextCleaner.Normalize("Hello\nworld") == "Hello world",
        "Latin lines should join with a space.");
    Assert(OcrTextCleaner.Normalize("你好\n世界") == "你好世界",
        "CJK lines should join without a space.");
    Assert(OcrTextCleaner.Normalize("  a   b  ") == "a b",
        "Runs of whitespace should collapse.");
    Assert(OcrTextCleaner.Normalize("") == string.Empty, "Empty input stays empty.");
    Assert(OcrTextCleaner.Normalize(null) == string.Empty, "Null input stays empty.");
    Assert(OcrTextCleaner.Normalize("你好，世界。Hello world!") == "你好，世界。Hello world!",
        "Punctuation adjacency should be preserved.");

    var overlong = new string('x', OcrTextCleaner.MaxTextLength + 50);
    Assert(OcrTextCleaner.Normalize(overlong).Length == OcrTextCleaner.MaxTextLength,
        "Overlong OCR text should be capped.");

    Assert(OcrTextCleaner.LooksChinese("请覆盖完整范围"), "Chinese text should map to zh->en.");
    Assert(!OcrTextCleaner.LooksChinese("The quick brown fox jumps over the lazy dog."),
        "English text should map to en->zh.");
    Assert(!OcrTextCleaner.LooksChinese("The quick brown fox jumps over the lazy dog. 请覆盖"),
        "Mostly-English mixed text should map to en->zh.");
    Assert(OcrTextCleaner.LooksChinese("请覆盖完整范围，确保所有条目。Hello"),
        "Mostly-Chinese mixed text should map to zh->en.");
});

RunAsync("Selection queries allow longer text but still prefer local", async () =>
{
    var online = new RecordingOnlineProvider();
    var lookup = new DictionaryLookup(new LocalDictionaryProvider(), online);

    const string longQuery = "This is a longer sentence fragment used for selection lookup testing";
    Assert(longQuery.Length > DictionaryLookup.MaxTypedQueryLength &&
           longQuery.Length <= DictionaryLookup.MaxSelectionQueryLength,
        "Test query length is wrong.");

    var typed = await lookup.FindAsync(longQuery, LookupDirection.EnglishToChinese, allowOnline: true, CancellationToken.None);
    Assert(!typed.Found, "Typed queries should still reject over-long input.");

    var selection = await lookup.FindSelectionAsync(longQuery, LookupDirection.EnglishToChinese, allowOnline: true, CancellationToken.None);
    Assert(selection.IsOnline && selection.Found, "Selection queries should reach the online provider.");
    Assert(online.Queries.SequenceEqual([longQuery]), "The selection text should be sent unchanged.");

    var tooLong = await lookup.FindSelectionAsync(
        new string('x', DictionaryLookup.MaxSelectionQueryLength + 1),
        LookupDirection.EnglishToChinese,
        allowOnline: true,
        CancellationToken.None);
    Assert(!tooLong.Found && tooLong.Error is not null, "Selection queries beyond the cap should be rejected.");
});

var realLexiconPath = Environment.GetEnvironmentVariable("POWERTY_LEXICON");
if (!string.IsNullOrEmpty(realLexiconPath) && File.Exists(realLexiconPath))
{
    RunSync("Real lexicon loads and answers both directions", () =>
    {
        var loadWatch = System.Diagnostics.Stopwatch.StartNew();
        var provider = LexiconDictionaryProvider.Load(realLexiconPath);
        loadWatch.Stop();
        Console.WriteLine($"     lexicon load: {loadWatch.ElapsedMilliseconds} ms, {provider.WordCount} words");
        Assert(provider.WordCount > 30000, $"Unexpected word count: {provider.WordCount}.");

        var cancel = provider.FindAsync("cancel", LookupDirection.EnglishToChinese, CancellationToken.None).GetAwaiter().GetResult();
        Assert(cancel.Count == 1 && cancel[0].Definitions.Count > 0, "cancel lookup failed.");

        var reverse = provider.FindAsync("苹果", LookupDirection.ChineseToEnglish, CancellationToken.None).GetAwaiter().GetResult();
        Assert(reverse.Count > 0, "Reverse lookup for 苹果 failed.");
        Assert(reverse.Any(entry => entry.Query == "apple"), $"Expected apple among: {string.Join(", ", reverse.Select(entry => entry.Query))}");

        var lookup = new DictionaryLookup(provider, new RecordingOnlineProvider());
        foreach (var (inflected, expected) in new[]
                 {
                     ("running", "run"),
                     ("returned", "return"),
                     ("studies", "study"),
                     ("quickly", "quick"),
                     ("building", "build")
                 })
        {
            var result = lookup.FindAsync(inflected, LookupDirection.EnglishToChinese, allowOnline: false, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert(result.Found, $"Real lexicon missed the base form for {inflected}.");
            Console.WriteLine($"     morphology: {inflected} -> {result.Entries[0].Query} (note: {expected})");
        }
    });
}

Console.WriteLine($"{passed} core checks passed.");

void RunSync(string name, Action test)
{
    test();
    passed++;
    Console.WriteLine($"PASS {name}");
}

void RunAsync(string name, Func<Task> test)
{
    test().GetAwaiter().GetResult();
    passed++;
    Console.WriteLine($"PASS {name}");
}

void RunStress(string lexiconPath)
{
    Console.WriteLine($"Loading lexicon: {lexiconPath}");
    var provider = LexiconDictionaryProvider.Load(lexiconPath);
    var samples = new[]
    {
        "I like to cancel the plan",
        "hello world again",
        "apple banana cherry",
        "govern the explosive market",
        "测试中文词条",
        "取消 预约 谢谢",
        "good morning everyone",
        "well-known fact",
        "don't stop now",
        "中文mixed内容"
    };
    const int iterations = 200_000;
    var failures = new List<string>();
    long queries = 0;

    GC.Collect();
    var memoryBefore = GC.GetTotalMemory(true);
    var watch = Stopwatch.StartNew();

    Parallel.For(0, iterations, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, _ =>
    {
        var text = samples[Random.Shared.Next(samples.Length)];
        var caret = Random.Shared.Next(text.Length + 1);
        foreach (var candidate in InputContextParser.Parse(text, caret))
        {
            Interlocked.Increment(ref queries);
            try
            {
                provider.FindAsync(candidate.Text, candidate.Direction, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                lock (failures)
                {
                    if (failures.Count < 10)
                    {
                        failures.Add($"{candidate.Text} ({candidate.Direction}): {exception.Message}");
                    }
                }
            }
        }
    });

    watch.Stop();
    GC.Collect();
    var memoryAfter = GC.GetTotalMemory(true);
    var perSecond = queries / Math.Max(0.001, watch.Elapsed.TotalSeconds);

    Console.WriteLine($"Stress: {iterations:N0} parse iterations, {queries:N0} queries in {watch.Elapsed.TotalSeconds:F1} s ({perSecond:N0} queries/s)");
    Console.WriteLine($"Stress memory: {memoryBefore / 1048576.0:F1} MB -> {memoryAfter / 1048576.0:F1} MB");
    Assert(failures.Count == 0, $"Stress failures:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    Assert(queries >= iterations / 2, "Too few queries were produced; the stress setup is broken.");
    Console.WriteLine("STRESS PASS");
}

const string ProbeDefaultWords =
    "went gone ran bought thought caught gave given took taken made said told felt kept left meant met paid sold sent sat slept spoke spoken spent stood swam swum taught threw thrown understood woke woken wore worn won wrote written " +
    "ate eaten drank drunk began begun chose chosen drove driven fell fallen forgot forgotten got gotten knew known lost rode ridden rose risen saw seen sang sung stole stolen broke broken brought built came did done drew drawn flew flown grew grown heard hid hidden held led showed shown stuck struck swore sworn became bent bit blew blown fed fought froze frozen hung laid lent lit quit rang sought shook shaken shut sank sunk slid spun split spread sprang stung swept swung tore torn upset wept wound withdrew withdrawn withheld " +
    "children men women feet teeth geese mice people leaves lives knives wives wolves shelves halves thieves " +
    "better best worse worst further furthest less least more most elder eldest " +
    "quickly slowly easily happily carefully really probably actually basically especially definitely unfortunately surprisingly immediately eventually finally usually generally exactly directly hardly merely simply totally completely absolutely obviously clearly recently currently originally typically " +
    "restaurant government environment beautiful yesterday tomorrow breakfast weather homework information education communication experience knowledge opportunity responsibility technology university hospital library difficult comfortable interesting exciting tired worried crowded pollution celebrate describe discuss explain imagine improve protect receive remember suggest translate " +
    "message meeting schedule deadline project report customer budget invoice contract address password account website software hardware network computer keyboard monitor button folder document presentation spreadsheet email attachment download upload update upgrade install uninstall error warning success failure " +
    "awesome amazing terrible exhausted gorgeous delicious frustrating curious nervous confident grateful apologize congratulations welcome anyway however therefore otherwise besides meanwhile although whether unless despite whereas " +
    "colleague manager salary interview resume application promotion resignation department strategy marketing revenue profit investment insurance mortgage landlord tenant grocery laundry commute traffic parking highway airport luggage passport visa " +
    "recharge battery charger earphone headphone refrigerator microwave cooker kettle shampoo towel blanket pillow mattress curtain drawer wardrobe balcony elevator corridor basement attic ceiling floor " +
    "translate translating translated translation translation's " +
    "eats eating eaten ate write writes writing written wrote read reads reading run runs running ran swim swims swimming swam swum speak speaks speaking spoke spoken " +
    "hi hey yeah nope ok okay thx pls btw btw idk lol asap " +
    "corner store desk chair table wall door roof window bed sofa lamp clock phone tablet laptop mouse screen speaker camera printer scanner router modem cable socket plug switch battery " +
    "chicken beef pork fish shrimp beef noodle rice soup salad sandwich burger pizza pasta coffee tea juice milk water beer wine dessert snack " +
    "rain snow wind cloud sunny cloudy rainy stormy thunder lightning temperature degree celsius fahrenheit humid dry wet chilly freezing boiling warm cool " +
    "yesterday today tomorrow morning afternoon evening night midnight weekend weekday holiday vacation festival birthday anniversary wedding funeral ceremony " +
    "doctor nurse patient medicine pill injection surgery fever cough headache toothache stomachache allergy symptom diagnosis treatment prescription pharmacy ambulance emergency " +
    "legal lawyer court judge jury evidence witness verdict appeal lawsuit patent copyright trademark license regulation compliance audit " +
    "physics chemistry biology geography history literature philosophy psychology sociology economics politics physics mathematics algebra geometry calculus statistics probability " +
    "sunny cloudy windy foggy snowy " +
    "goes going went does did done doing has have had having is are was were been being am be "; 

void RunDump(string lexiconPath, string query)
{
    var provider = LexiconDictionaryProvider.Load(lexiconPath);
    var direction = query.Any(character => character > 0x7F)
        ? LookupDirection.ChineseToEnglish
        : LookupDirection.EnglishToChinese;
    var results = provider.FindAsync(query, direction, CancellationToken.None).GetAwaiter().GetResult();
    Console.WriteLine($"DUMP {query} ({direction}) count={results.Count}");
    foreach (var entry in results)
    {
        Console.WriteLine($"  {entry.Query}: {string.Join(" | ", entry.Definitions)}");
    }
}

void RunProbe(string lexiconPath, string? wordsFile)
{
    var provider = LexiconDictionaryProvider.Load(lexiconPath);
    var lookup = new DictionaryLookup(provider, new ThrowingOnlineProvider(new HttpRequestException("probe")));
    var words = new List<string>();
    if (wordsFile is not null && File.Exists(wordsFile))
    {
        words.AddRange(File.ReadAllLines(wordsFile).Select(line => line.Trim()).Where(line => line.Length > 0));
    }
    else
    {
        words.AddRange(ProbeDefaultWords.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    var hits = new List<(string Word, string Matched, int Count)>();
    var misses = new List<string>();
    foreach (var word in words)
    {
        var direction = word.Any(character => character > 0x7F)
            ? LookupDirection.ChineseToEnglish
            : LookupDirection.EnglishToChinese;
        var result = lookup.FindAsync(word, direction, allowOnline: false, CancellationToken.None)
            .GetAwaiter().GetResult();
        if (result.Found)
        {
            hits.Add((word, result.Entries[0].Query, result.Entries.Count));
        }
        else
        {
            misses.Add(word);
        }
    }

    Console.WriteLine($"PROBE lex={lexiconPath} total={words.Count} hits={hits.Count} misses={misses.Count} cover={(words.Count == 0 ? 0 : 100.0 * hits.Count / words.Count):F1}%");
    Console.WriteLine($"  lex load stats: words={provider.WordCount} gc={GC.GetTotalMemory(true) / 1048576.0:F1} MB");
    foreach (var (word, matched, count) in hits)
    {
        Console.WriteLine($"  HIT  {word} -> {matched} ({count})");
    }

    Console.WriteLine("  MISSING: " + (misses.Count == 0 ? "(none)" : string.Join(" ", misses)));
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static bool LoadFails(byte[] payload)
{
    try
    {
        using var stream = new MemoryStream(payload);
        LexiconDictionaryProvider.Load(stream);
        return false;
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException)
    {
        return true;
    }
}

static LexiconDictionaryProvider BuildProvider(params LexiconWord[] words)
{
    using var stream = new MemoryStream();
    LexiconFormat.Save(stream, words);
    stream.Position = 0;
    return LexiconDictionaryProvider.Load(stream);
}

sealed class EmptyLocalProvider : ILocalDictionaryProvider
{
    public ValueTask<IReadOnlyList<DictionaryEntry>> FindAsync(
        string query,
        LookupDirection direction,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<DictionaryEntry>>([]);
}

sealed class RecordingOnlineProvider : IOnlineLookupProvider
{
    public List<string> Queries { get; } = [];

    public Task<IReadOnlyList<DictionaryEntry>> FindAsync(
        string query,
        LookupDirection direction,
        CancellationToken cancellationToken)
    {
        Queries.Add(query);
        return Task.FromResult<IReadOnlyList<DictionaryEntry>>(
            [new DictionaryEntry(query, direction, ["测试释义"], "测试在线源")]);
    }
}

sealed class ThrowingOnlineProvider(Exception error) : IOnlineLookupProvider
{
    public Task<IReadOnlyList<DictionaryEntry>> FindAsync(
        string query,
        LookupDirection direction,
        CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<DictionaryEntry>>(error);
}

sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<string> RequestUris { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestUris.Add(request.RequestUri!.ToString());
        return Task.FromResult(responder(request));
    }
}
