using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Nodes;

/// <summary>
/// Shared configuration of the AR/BE write-back nodes. Both consume the DilosFileFetch
/// document <c>{ fileName, content }</c> and write to WeClapp with the fetch node's
/// retry discipline (transient 5xx/408/429/network retried with exponential backoff,
/// everything else fail-loud).
/// </summary>
public abstract record WeClappWriteNodeConfiguration : NodeConfiguration
{
    /// <summary>WeClapp API base, e.g. "https://{tenant}.weclapp.com/webapp/api/v1".</summary>
    public required string BaseUrl { get; set; }

    /// <summary>WeClapp API token (sent as "AuthenticationToken" header). Comes from the
    /// pipeline deployment configuration — never hardcode or log it.</summary>
    public required string ApiKey { get; set; }

    /// <summary>Data context path of the DILOS file name (trigger document contract).</summary>
    public string FileNamePath { get; set; } = "$.fileName";

    /// <summary>Data context path of the raw DILOS file content (trigger document contract).</summary>
    public string ContentPath { get; set; } = "$.content";

    /// <summary>Validate without persisting: PUT/POST calls that support WeClapp's
    /// <c>?dryRun=true</c> run with it; calls without dry-run support are skipped and logged.</summary>
    public bool DryRun { get; set; }

    /// <summary>Retry attempts for transient HTTP failures (5xx/408/429/network), exponential backoff.</summary>
    public int MaxRetries { get; set; } = 4;

    /// <summary>Backoff base in seconds (delay = base * 2^(attempt-1)); tests set 0.</summary>
    public double RetryBackoffBaseSeconds { get; set; } = 1;
}
