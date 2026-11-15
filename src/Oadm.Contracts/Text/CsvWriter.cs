using System.Text;

namespace Oadm.Contracts.Text;

/// <summary>
/// RFC 4180 CSV records, the one writer of the client's exports and the server's file tools: comma separated, fields with a
/// comma, quote or line break in double quotes (quotes doubled), CRLF between records; a cell that starts with = + - @ (or a
/// tab / carriage return) gets a leading apostrophe so a spreadsheet never runs it as a formula.
/// </summary>
public static class CsvWriter
{
    /// <summary>One cell as written: formula-safe and quoted when needed.</summary>
    public static string Field(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    /// <summary>Appends one record (cells and CRLF).</summary>
    public static void AppendRecord(StringBuilder sb, IEnumerable<string?> cells)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(cells);
        var first = true;
        foreach (var cell in cells)
        {
            if (!first)
            {
                sb.Append(',');
            }

            sb.Append(Field(cell));
            first = false;
        }

        sb.Append("\r\n");
    }

    /// <summary>The text as a file: UTF-8 with BOM, so Excel reads it.</summary>
    public static byte[] ToBytes(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bom = Encoding.UTF8.GetPreamble();
        var data = new byte[bom.Length + Encoding.UTF8.GetByteCount(text)];
        bom.CopyTo(data, 0);
        Encoding.UTF8.GetBytes(text, 0, text.Length, data, bom.Length);
        return data;
    }
}
