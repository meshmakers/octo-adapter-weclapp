using System.Globalization;

namespace Lkv.WeClapp.Core.Dilos;

/// <summary>
/// Value-format helpers for DILOS return files (AR/BE): comma decimal separator
/// (unlike AI/AS which use dot!), dates as dd.MM.yyyy, CRLF records.
/// </summary>
internal static class DilosFormat
{
    private static readonly NumberFormatInfo CommaDecimal = new()
    {
        NumberDecimalSeparator = ",",
        NegativeSign = "-",
    };

    /// <summary>Splits content into non-empty lines with physical 1-based line numbers.</summary>
    public static IEnumerable<(string Line, int Number)> DataLines(string content) =>
        content.Split('\n')
            .Select((raw, i) => (Line: raw.TrimEnd('\r'), Number: i + 1))
            .Where(t => t.Line.Length > 0);

    /// <summary>Mandatory decimal, comma separator, optional leading sign. Dot fails loud.</summary>
    public static decimal Dec(string value, int lineNumber, string field) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CommaDecimal, out var d)
            ? d
            : throw new DilosParseException(lineNumber, $"Field '{field}' is not a DILOS number: '{value}'");

    /// <summary>Optional decimal: empty → null.</summary>
    public static decimal? OptDec(string value, int lineNumber, string field) =>
        value.Length == 0 ? null : Dec(value, lineNumber, field);

    /// <summary>Optional integer: empty → null.</summary>
    public static int? OptInt(string value, int lineNumber, string field) =>
        value.Length == 0
            ? null
            : int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i)
                ? i
                : throw new DilosParseException(lineNumber, $"Field '{field}' is not an integer: '{value}'");

    /// <summary>Optional date, exactly dd.MM.yyyy (DILOS "TT.MM.JJJJ"): empty → null.</summary>
    public static DateOnly? OptDate(string value, int lineNumber, string field) =>
        value.Length == 0
            ? null
            : DateOnly.TryParseExact(value, "dd.MM.yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d)
                ? d
                : throw new DilosParseException(lineNumber, $"Field '{field}' is not a dd.MM.yyyy date: '{value}'");
}
