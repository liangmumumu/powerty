namespace Powerty.Core;

/// <summary>
/// The online fallback rejected the request because its free daily quota is
/// exhausted (HTTP 429). Kept separate from transport errors so callers can show
/// an accurate message and back off instead of retrying immediately.
/// </summary>
public sealed class OnlineLookupQuotaException(string message) : Exception(message);
