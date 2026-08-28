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
    /// precedent AS20240206020204.txt).</summary>
    public static string AsFileName(DateTimeOffset utcNow) => DeliveryFileName("AS", utcNow);

    /// <summary>The name of one timestamp-stamped batch delivery: the export kind, the
    /// Vienna-local yyyyMMddHHmmss stamp and ".txt". Vienna because DILOS runs Austrian local
    /// time - a UTC stamp would date a late-evening delivery to the previous day. Invariant
    /// culture so non-Gregorian process cultures cannot distort the 14-digit stamp. The AI
    /// delivery is named per ORDER instead and lives in <see cref="AiFileName"/>.</summary>
    public static string DeliveryFileName(string exportKind, DateTimeOffset utcNow)
    {
        var vienna = TimeZoneInfo.ConvertTime(utcNow, ViennaTime.Zone);
        return $"{exportKind}{vienna.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.txt";
    }

    /// <summary>A DILOS file name is a bare name, never a path. The delivery node resolves a name
    /// carrying path segments to its LAST segment and uploads under that name without complaining,
    /// so a poisoned value would travel silently; callers reject it instead. The rule lives here
    /// because it belongs to the name conventions above, the failure type to the caller.</summary>
    public static bool IsPlainFileName(string fileName) =>
        !fileName.Contains('/') && !fileName.Contains('\\') && !fileName.Contains("..");
}
