using System.Text;

namespace Powerty.Core;

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
            _text.Remove(0, _text.Length - _capacity);
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
