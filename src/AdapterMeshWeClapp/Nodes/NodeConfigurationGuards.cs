namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Configuration guards shared by the adapter's nodes, so the same defect cannot be answered
/// differently in two places.
/// </summary>
internal static class NodeConfigurationGuards
{
    /// <summary>
    /// Refuses a path property that names nothing. The properties are non-nullable and carry an
    /// initializer, but a yaml line with no value ("path:") assigns null OVER that initializer, so
    /// blank is a state a pipeline definition really produces - and every node has to answer it
    /// before it reads or writes anything. Unguarded, a blank READ path surfaces as a raw
    /// NullReferenceException that names nothing, and a blank WRITE path does not fail at all: the
    /// data context treats null and empty alike as "$" and the value replaces the document root.
    /// </summary>
    internal static void RequirePath(string nodeName, string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new WeClappPipelineExecutionException(
                $"{nodeName}: '{propertyName}' must be a JSONPath");
        }
    }
}
