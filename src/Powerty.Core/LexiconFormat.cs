using System.IO;
using System.Text;

namespace Powerty.Core;

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
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var magic = reader.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("无法识别的词库文件格式。");
        }

        var version = reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"不支持的词库文件版本：{version}。");
        }

        var count = reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException("词库文件已损坏。");
        }

        var words = new List<LexiconWord>(count);
        for (var index = 0; index < count; index++)
        {
            var key = ReadString(reader);
            var name = ReadString(reader);
            var definitionCount = reader.ReadByte();
            var definitions = new string[definitionCount];
            for (var definitionIndex = 0; definitionIndex < definitionCount; definitionIndex++)
            {
                definitions[definitionIndex] = ReadString(reader);
            }

            var usPhonetic = ReadShortString(reader);
            var ukPhonetic = ReadShortString(reader);
            var frequency = reader.ReadInt32();
            words.Add(new LexiconWord(key, name, definitions, usPhonetic, ukPhonetic, frequency));
        }

        return words;
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

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt16();
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new InvalidDataException("词库文件已损坏。");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static string ReadShortString(BinaryReader reader)
    {
        var length = reader.ReadByte();
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new InvalidDataException("词库文件已损坏。");
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
