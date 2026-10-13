using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Qwaacty.Core;

public sealed class AiTranslatorOptions(string baseUrl, string apiKey, string model)
{
    public string BaseUrl { get; } = baseUrl;
    public string ApiKey { get; } = apiKey;
    public string Model { get; } = model;

    /// <summary>Masks the API key: the default record ToString() would print it in plain
    /// text wherever an interpolated string is used.</summary>
    public override string ToString() =>
        $"AiTranslatorOptions {{ BaseUrl = {BaseUrl}, Model = {Model}, ApiKey = *** }}";
}

public sealed class OpenAiCompatibleTranslator(HttpClient httpClient)
{
    public async IAsyncEnumerable<string> StreamAsync(
        AiTranslatorOptions options,
        string query,
        LookupDirection direction,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var chunk in StreamWithPromptAsync(
                           options,
                           BuildPrompt(direction),
                           query,
                           maxTokens: 220,
                           temperature: 0.2,
                           cancellationToken))
        {
            yield return chunk;
        }
    }

    public async IAsyncEnumerable<string> StreamWithPromptAsync(
        AiTranslatorOptions options,
        string systemPrompt,
        string userPrompt,
        int maxTokens,
        double temperature,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var uri = CombineUrl(options.BaseUrl, "chat/completions");
        // Guard against malformed base URLs (a missing scheme makes HttpClient throw)
        // and against sending the API key in cleartext: only https, or loopback http
        // (for local servers like Ollama / LM Studio), is accepted.
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps &&
             !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)))
        {
            throw new InvalidOperationException(
                "AI 接口地址无效：请填写以 https:// 开头的完整地址（本机服务可用 http://127.0.0.1 或 http://localhost）。");
        }

        var payload = new
        {
            model = options.Model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            max_tokens = maxTokens,
            temperature,
            stream = true
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {options.ApiKey}");
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // ReadLineAsync (null at end of stream) instead of StreamReader.EndOfStream:
        // EndOfStream performs a *synchronous* read on the underlying network stream
        // whenever its buffer runs dry, and because this iterator runs on the caller's
        // synchronization context that read froze the WPF UI thread until the next SSE
        // packet (or the final connection close) arrived — measured as 100–350 ms cursor
        // stutters during streaming AI translations. ReadLineAsync never blocks a thread.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            var chunk = ParseStreamChunk(line);
            if (!string.IsNullOrEmpty(chunk))
            {
                yield return chunk;
            }
        }
    }

    public static string? ParseStreamChunk(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var trimmed = line.Trim();
        if (!trimmed.StartsWith("data:", StringComparison.Ordinal))
        {
            return null;
        }

        var data = trimmed[5..].Trim();
        if (data.Length == 0 || data == "[DONE]")
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                // Gateways occasionally return bare JSON scalars/arrays ("data: null");
                // TryGetProperty would throw InvalidOperationException on them.
                return null;
            }

            if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return null;
            }

            var first = choices[0];
            if (!first.TryGetProperty("delta", out var delta) ||
                !delta.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return content.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        return $"{trimmed}/{path}";
    }

    private static string BuildPrompt(LookupDirection direction) => direction == LookupDirection.EnglishToChinese
        ? "你是中英词典助手。用户会给你英文内容。规则：1) 如果是单词或短语：按词性给出简明中文释义，最多4项，用「；」分隔，格式如「n. 苹果；vt. 做」。2) 如果是完整句子：给出通顺自然的简体中文翻译。3) 只输出翻译内容本身，不要解释、不要引号、不要任何前缀。"
        : "你是中英词典助手。用户会给你中文内容。规则：1) 如果是词语：给出最常用的英文对应词，最多5个，用「；」分隔。2) 如果是完整句子：给出自然流畅的英文翻译。3) 只输出译文本身，不要解释、不要引号、不要任何前缀。";
}
