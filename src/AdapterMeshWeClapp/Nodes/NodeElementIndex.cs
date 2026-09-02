namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Indexes a fetched WeClapp entity array by its id, so the two nodes that resolve a reference
/// against a separately fetched entity set answer the same defects the same way. Both joins have
/// the identical failure shape: an entity that cannot be keyed is unreachable, and a reference
/// aimed at it then resolves to nothing - which is indistinguishable from "this record has no
/// such value" once the DILOS file is written (EK-Preis 0 for the article master, an empty MwSt
/// field for an order position). So both are loud here, where the element index still exists to
/// name, rather than later where only the missing value is left.
/// </summary>
internal static class NodeElementIndex
{
    /// <summary>
    /// Builds the id → entity map, refusing an entity without an id and a second entity under an
    /// id already taken. The duplicate guard doubles as the paging cross-check: two pages that
    /// overlap deliver the same entity twice, and the run says so instead of quietly keeping
    /// whichever copy arrived first.
    /// </summary>
    /// <param name="elements">The fetched array, exactly as the data context handed it over.</param>
    /// <param name="idOf">Reads the id off one element.</param>
    /// <param name="nodeName">Node the message is attributed to.</param>
    /// <param name="path">JSONPath the array was read from - the thing an operator can edit.</param>
    /// <param name="what">Entity name for the duplicate message ("articleSupplySource", "tax").</param>
    /// <param name="unreachable">Why an unkeyable entity matters, in this node's terms.</param>
    internal static Dictionary<string, T> ById<T>(IEnumerable<T?> elements, Func<T, string?> idOf,
        string nodeName, string path, string what, string unreachable)
        where T : class
    {
        var byId = new Dictionary<string, T>(StringComparer.Ordinal);
        var index = -1;

        foreach (var candidate in elements)
        {
            index++;
            if (candidate is not { } element || idOf(element) is not { Length: > 0 } id)
            {
                throw new WeClappPipelineExecutionException(
                    $"{nodeName}: entity {index} at '{path}' carries no 'id' - {unreachable}");
            }

            if (!byId.TryAdd(id, element))
            {
                throw new WeClappPipelineExecutionException(
                    $"{nodeName}: {what} id '{id}' appears more than once at '{path}' - the " +
                    "resolution would be ambiguous");
            }
        }

        return byId;
    }
}
