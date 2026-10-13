using System.Text;

namespace Qwaacty.Core;

public sealed class TypingBuffer
{
    private const int DefaultCapacity = 64;

    private readonly StringBuilder _text = new();
    private readonly int _capacity;

    public TypingBuffer(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(8, capacity);
    }

    public int Length => _text.Length;

    public void Append(char character)
    {
        if (char.IsControl(character))
        {
            return;
        }

        _text.Append(character);
        if (_text.Length > _capacity)
        {
            var cut = _text.Length - _capacity;
            if (char.IsLowSurrogate(_text[cut]))
            {
                // Never split a surrogate pair at the window edge.
                cut++;
            }

            _text.Remove(0, cut);
        }
    }

    public void Backspace()
    {
        if (_text.Length > 0)
        {
            _text.Length--;
        }
    }

    public void Clear() => _text.Clear();

    public string Snapshot() => _text.ToString();
}
