using System.Text;

namespace Conduit.Readers.Csv;

/// <summary>
/// The CSV this suite writes: RFC 4180 fields (quoted when they hold a comma, quote, line break or edge space; quotes
/// doubled) and CRLF lines, the same dialect <see cref="CsvRecordReader"/> reads back. A field that begins with a
/// spreadsheet formula leader (= + - @, tab or carriage return) is written with a leading apostrophe so a spreadsheet
/// shows it as text and never runs it: exported values carry text other people typed.
/// </summary>
public static class CsvText
{
    private static readonly char[] FormulaLeaders = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>One field, quoted and neutralised as needed. Null is an empty field.</summary>
    public static string Field(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && Array.IndexOf(FormulaLeaders, text[0]) >= 0) text = "'" + text;
        var quote = text.IndexOfAny([',', '"', '\r', '\n']) >= 0 || (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])));
        return quote ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    /// <summary>One line of fields, ending in CRLF.</summary>
    public static string Line(IEnumerable<string?> fields) => string.Join(",", fields.Select(Field)) + "\r\n";

    /// <summary>A whole document: the header line, then one line per row.</summary>
    public static string Document(IEnumerable<string> header, IEnumerable<IEnumerable<string?>> rows)
    {
        var text = new StringBuilder(Line(header));
        foreach (var row in rows) text.Append(Line(row));
        return text.ToString();
    }
}
