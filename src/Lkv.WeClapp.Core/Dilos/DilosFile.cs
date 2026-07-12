using System.Globalization;
using System.Text;

namespace Lkv.WeClapp.Core.Dilos;

/// <summary>
/// DILOS file-level conventions shared by both transfer directions: golden file names
/// and the file encoding. Centralised here so the name never diverges from the K* line
/// (Auftragsnummer1) and the encoding never diverges between write and read.
/// </summary>
public static class DilosFile
{
    /// <summary>
    /// ISO-8859-1 — the encoding of every golden (DILOS-import-proven) file. Characters
    /// outside Latin-1 encode as '?'; callers that write content should detect and report
    /// them (never silently).
    /// </summary>
    public static readonly Encoding Encoding = System.Text.Encoding.GetEncoding("ISO-8859-1",
        new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);

    /// <summary>Golden AI name: "AI" + Auftragsnummer1 + ".txt" (Billbee precedent
    /// AI5910748889425.txt). Auftragsnummer1 is the WeClapp id — the SAME number the
    /// K* line carries; the shop orderNumber (Auftragsnummer2) must not name the file.</summary>
    public static string AiFileName(string auftragsnummer1) => $"AI{auftragsnummer1}.txt";

    /// <summary>Golden AS name: "AS" + Vienna-local yyyyMMddHHmmss + ".txt" (Billbee
    /// precedent AS20240206020204.txt). Vienna because DILOS runs Austrian local time — a
    /// UTC stamp would date late-evening polls to the previous day. Invariant culture so
    /// non-Gregorian process cultures cannot distort the 14-digit stamp.</summary>
    public static string AsFileName(DateTimeOffset utcNow)
    {
        var vienna = TimeZoneInfo.ConvertTime(utcNow, ViennaTime.Zone);
        return $"AS{vienna.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.txt";
    }
}
