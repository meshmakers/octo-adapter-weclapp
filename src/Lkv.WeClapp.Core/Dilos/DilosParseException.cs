namespace Lkv.WeClapp.Core.Dilos;

/// <summary>Structural defect in a DILOS file (AR/BE). Fail-loud: no silent skipping.</summary>
/// <param name="lineNumber">Physical 1-based line number in the parsed content.</param>
/// <param name="message">Defect description; the line number is prefixed automatically.</param>
public class DilosParseException(int lineNumber, string message)
    : Exception($"Line {lineNumber}: {message}")
{
    /// <summary>Physical 1-based line number the defect was found at.</summary>
    public int LineNumber { get; } = lineNumber;
}
