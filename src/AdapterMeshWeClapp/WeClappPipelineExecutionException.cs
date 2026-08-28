namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp;

/// <summary>
/// Pipeline execution defect in the WeClapp adapter (configuration or data contract
/// violation). Fail-loud per project convention — the pipeline run fails visibly
/// instead of producing silently wrong DILOS files or CK entities.
/// </summary>
/// <remarks>
/// The optional inner exception exists for the guards that re-throw a raw framework failure
/// (System.Text.Json, LINQ) under a message naming the node and the element: the original stack
/// is what tells an operator WHICH property of the payload was unusable.
/// </remarks>
public class WeClappPipelineExecutionException(string message, Exception? innerException = null)
    : Exception(message, innerException);
