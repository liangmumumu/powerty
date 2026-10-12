using System.Text;

/// <summary>
/// Minimal RFC 4180 CSV reader for the ECDICT export: handles quoted fields,
/// embedded commas/newlines inside quotes and escaped double quotes.
/// </summary>
internal static class EcdictCsv
{
    public static IEnumerable<string[]> ReadRows(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20);
        var fields = new List<string>(16);
        var field = new StringBuilder(128);
        var inQuotes = false;
        var rowHasContent = false;
        while (true)
        {
            var value = reader.Read();
            if (value < 0)
            {
                if (rowHasContent || field.Length > 0)
                {
                    fields.Add(field.ToString());
                    yield return fields.ToArray();
                }

                yield break;
            }

            var character = (char)value;
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        field.Append('"');
                        reader.Read();
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inQuotes = true;
                    rowHasContent = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    rowHasContent = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    if (rowHasContent)
                    {
                        yield return fields.ToArray();
                    }

                    fields.Clear();
                    rowHasContent = false;
                    break;
                default:
                    field.Append(character);
                    rowHasContent = true;
                    break;
            }
        }
    }
}

internal sealed record EcdictRow(
    string Word,
    string Phonetic,
    string Translation,
    int Collins,
    int Oxford,
    string Tag,
    int Bnc,
    int Frq);
