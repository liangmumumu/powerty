using System.Text.Json;

namespace Powerty.Core;

public sealed class DictionaryLookup(
    ILocalDictionaryProvider localProvider,
    IOnlineLookupProvider onlineProvider) : IDictionaryLookup
{
    public const int MaxTypedQueryLength = 48;
    public const int MaxSelectionQueryLength = 120;

    public Task<LookupResponse> FindAsync(
        string query,
        LookupDirection direction,
        bool allowOnline,
        CancellationToken cancellationToken) =>
        FindCoreAsync(query, direction, allowOnline, MaxTypedQueryLength, allowChineseLadder: true, cancellationToken);

    public Task<LookupResponse> FindSelectionAsync(
        string query,
        LookupDirection direction,
        bool allowOnline,
        CancellationToken cancellationToken) =>
        FindCoreAsync(query, direction, allowOnline, MaxSelectionQueryLength, allowChineseLadder: false, cancellationToken);

    private static readonly char[] QueryTrimChars =
        ['.', ',', '!', '?', ';', ':', '"', '\'', '“', '”', '‘', '’', '(', ')', '（', '）',
         '[', ']', '【', '】', '-', '—', '…', '。', '，', '！', '？', '；', '：', '、', ' ', '\u3000'];

    private static string TrimQuery(string query) => query.Trim().Trim(QueryTrimChars);

    private async Task<LookupResponse> FindCoreAsync(
        string query,
        LookupDirection direction,
        bool allowOnline,
        int maxLength,
        bool allowChineseLadder,
        CancellationToken cancellationToken)
    {
        var normalized = TrimQuery(query);
        if (normalized.Length is 0 || normalized.Length > maxLength)
        {
            return new([], false, normalized.Length == 0 ? null : "查询内容过长。");
        }

        var localEntries = await localProvider.FindAsync(normalized, direction, cancellationToken);
        if (localEntries.Count == 0 && direction == LookupDirection.EnglishToChinese)
        {
            foreach (var variant in EnglishMorphology.Candidates(normalized))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var variantEntries = await localProvider.FindAsync(variant, direction, cancellationToken);
                if (variantEntries.Count > 0)
                {
                    localEntries = variantEntries;
                    break;
                }
            }
        }

        if (localEntries.Count == 0 && allowChineseLadder && direction == LookupDirection.ChineseToEnglish)
        {
            foreach (var suffix in ChineseQueryLadder.Suffixes(normalized))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var suffixEntries = await localProvider.FindAsync(suffix, direction, cancellationToken);
                if (suffixEntries.Count > 0)
                {
                    localEntries = suffixEntries;
                    break;
                }
            }
        }

        if (localEntries.Count > 0 || !allowOnline)
        {
            return new(localEntries, false);
        }

        try
        {
            var onlineEntries = await onlineProvider.FindAsync(normalized, direction, cancellationToken);
            return new(onlineEntries, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OnlineLookupQuotaException exception)
        {
            return new([], true, exception.Message);
        }
        catch (HttpRequestException)
        {
            return new([], true, "在线查词暂不可用，请检查网络后重试。");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new([], true, "在线查词超时。");
        }
        catch (JsonException)
        {
            return new([], true, "在线查词返回了无法识别的结果。");
        }
        catch (IOException)
        {
            return new([], true, "读取在线查词结果失败。");
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
