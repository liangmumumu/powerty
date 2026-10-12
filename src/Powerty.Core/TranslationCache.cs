namespace Powerty.Core;

public sealed class TranslationCache(int capacity = 300)
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Query, LookupDirection Direction), LinkedListNode<Entry>> _map = [];
    private readonly LinkedList<Entry> _order = new();
    private readonly int _capacity = Math.Max(16, capacity);

    private sealed record Entry(string Query, LookupDirection Direction, string Text);

    public bool TryGet(string query, LookupDirection direction, out string? text)
    {
        lock (_gate)
        {
            if (_map.TryGetValue((query, direction), out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                text = node.Value.Text;
                return true;
            }
        }

        text = null;
        return false;
    }

    public void Set(string query, LookupDirection direction, string text)
    {
        lock (_gate)
        {
            if (_map.TryGetValue((query, direction), out var existing))
            {
                _order.Remove(existing);
                _map.Remove((query, direction));
            }

            var node = _order.AddFirst(new Entry(query, direction, text));
            _map[(query, direction)] = node;
            while (_map.Count > _capacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove((last.Value.Query, last.Value.Direction));
            }
        }
    }
}
