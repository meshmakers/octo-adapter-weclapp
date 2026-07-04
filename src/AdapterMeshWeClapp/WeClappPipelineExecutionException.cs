namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp;

/// <summary>
/// Pipeline execution defect in the WeClapp adapter (configuration or data contract
/// violation). Fail-loud per project convention — the pipeline run fails visibly
/// instead of producing silently wrong DILOS files or CK entities.
/// </summary>
public class WeClappPipelineExecutionException(string message) : Exception(message);
