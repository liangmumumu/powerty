using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Powerty.Core;

public sealed class MyMemoryLookupProvider(HttpClient httpClient) : IOnlineLookupProvider
{
    private const int MaxQueryLength = 120;
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex QuotaDelayPattern = new(
        @"NEXT AVAILABLE IN\s+(?:(\d+)\s+HOURS?\s*)?(?:(\d+)\s+MINUTES?)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _lastRequestUtc;

    /// <summary>
    /// Builds the user-facing message for a quota rejection. The service reports
    /// the wait as "NEXT AVAILABLE IN 16 HOURS 46 MINUTES 42 SECONDS" in its body.
    /// </summary>
    public static string DescribeQuotaLimit(string? responseBody)
    {
        var wait = "稍后";
        if (!string.IsNullOrEmpty(responseBody))
        {
            var match = QuotaDelayPattern.Match(responseBody);
            if (match.Success)
            {
                var hours = match.Groups[1].Success ? match.Groups[1].Value : "0";
                var minutes = match.Groups[2].Success ? match.Groups[2].Value : "0";
                wait = $"{hours} 小时 {minutes} 分后";
            }
        }

        return $"MyMemory 免费在线翻译额度已用完（约 {wait} 恢复）。" +
               "建议在设置中配置 AI 翻译（如 DeepSeek）以获得稳定的整句翻译。";
    }

    public async Task<IReadOnlyList<DictionaryEntry>> FindAsync(
        string query,
        LookupDirection direction,
        CancellationToken cancellationToken)
    {
        var normalized = query.Trim();
        if (normalized.Length is 0 or > MaxQueryLength)
        {
            return [];
        }

        var languages = direction == LookupDirection.EnglishToChinese ? "en|zh-CN" : "zh-CN|en";
        var uri = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(normalized)}&langpair={languages}";
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var remainingDelay = MinimumRequestInterval - (DateTimeOffset.UtcNow - _lastRequestUtc);
            if (_lastRequestUtc != default && remainingDelay > TimeSpan.Zero)
            {
                await Task.Delay(remainingDelay, cancellationToken);
            }

            _lastRequestUtc = DateTimeOffset.UtcNow;
            using var response = await httpClient.GetAsync(uri, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new OnlineLookupQuotaException(DescribeQuotaLimit(body));
            }

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<MyMemoryResponse>(stream, JsonOptions, cancellationToken);
            var translated = payload?.ResponseData?.TranslatedText?.Trim();
            if (string.IsNullOrWhiteSpace(translated) ||
                string.Equals(translated, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            return
            [
                new DictionaryEntry(
                    normalized,
                    direction,
                    [translated],
                    "MyMemory 在线翻译")
            ];
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private sealed class MyMemoryResponse
    {
        public ResponseData? ResponseData { get; init; }
    }

    private sealed class ResponseData
    {
        public string? TranslatedText { get; init; }
    }
}
