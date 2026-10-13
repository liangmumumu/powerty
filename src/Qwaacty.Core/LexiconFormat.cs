using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Qwaacty.Core;

public sealed record LexiconWord(
    string Key,
    string Name,
    IReadOnlyList<string> Definitions,
    string UsPhonetic,
    string UkPhonetic,
    int Frequency = 0);

public static class LexiconFormat
{
    public const byte FormatVersion = 2;

    private static ReadOnlySpan<byte> Magic => "TTLX"u8;

    public static void Save(Stream stream, IReadOnlyList<LexiconWord> words)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(words.Count);
        foreach (var word in words)
        {
            WriteString(writer, word.Key);
            WriteString(writer, word.Name);
            if (word.Definitions.Count > byte.MaxValue)
            {
                throw new InvalidDataException("释义数量超出格式限制。");
            }

            writer.Write((byte)word.Definitions.Count);
            foreach (var definition in word.Definitions)
            {
                WriteString(writer, definition);
            }

            WriteShortString(writer, word.UsPhonetic);
            WriteShortString(writer, word.UkPhonetic);
            // Corpus frequency rank (0 = unknown); used by reverse-lookup ranking so
            // common words are preferred over obscure shorter ones.
            writer.Write(word.Frequency);
        }
    }

    public static List<LexiconWord> Load(Stream stream)
    {
        // Parse from one buffer instead of BinaryReader.ReadBytes per string: a
        // 432k-word lexicon used to allocate ~2.5M temporary byte[] arrays at startup
        // (one per word/definition/phonetic string), which dominated both load time and
        // GC pressure. Span slicing removes those intermediate allocations.
        byte[] data;
        using (var memory = new MemoryStream(stream.CanSeek ? (int)Math.Max(0, stream.Length - stream.Position) : 0))
        {
            stream.CopyTo(memory);
            data = memory.ToArray();
        }

        try
        {
            return LoadCore(data);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Malformed payloads used to surface as InvalidDataException from the
            // explicit header checks; keep that contract for truncated bodies too.
            throw new InvalidDataException("词库文件已损坏。", exception);
        }
    }

    private static List<LexiconWord> LoadCore(byte[] data)
    {
        var span = new ReadOnlySpan<byte>(data);
        if (span.Length < 9 || !span[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("无法识别的词库文件格式。");
        }

        var version = span[4];
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"不支持的词库文件版本：{version}。");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(span[5..]);
        if (count < 0)
        {
            throw new InvalidDataException("词库文件已损坏。");
        }

        // Sanity bound: every entry occupies at least ~9 bytes on disk, so a count
        // larger than the remaining file size cannot be genuine — a corrupted header
        // must not make List preallocate gigabytes and crash the app at startup.
        if (count > (span.Length - 9) / 9 + 1)
        {
            throw new InvalidDataException("词库文件已损坏（条目数异常）。");
        }

        var offset = 9;
        var words = new List<LexiconWord>(count);
        for (var index = 0; index < count; index++)
        {
            var key = ReadString(span, ref offset);
            var name = ReadString(span, ref offset);
            var definitionCount = span[offset++];
            var definitions = new string[definitionCount];
            for (var definitionIndex = 0; definitionIndex < definitionCount; definitionIndex++)
            {
                definitions[definitionIndex] = ReadString(span, ref offset);
            }

            var usPhonetic = ReadShortString(span, ref offset);
            var ukPhonetic = ReadShortString(span, ref offset);
            var frequency = BinaryPrimitives.ReadInt32LittleEndian(span[offset..]);
            offset += 4;
            words.Add(new LexiconWord(key, name, definitions, usPhonetic, ukPhonetic, frequency));
        }

        return words;
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        var value = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return value;
    }

    private static string ReadShortString(ReadOnlySpan<byte> data, ref int offset)
    {
        var length = data[offset++];
        var value = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return value;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue)
        {
            throw new InvalidDataException("词库文本超出格式限制。");
        }

        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteShortString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > byte.MaxValue)
        {
            throw new InvalidDataException("音标文本超出格式限制。");
        }

        writer.Write((byte)bytes.Length);
        writer.Write(bytes);
    }
}
