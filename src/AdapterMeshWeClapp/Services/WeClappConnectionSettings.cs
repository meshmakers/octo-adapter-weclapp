using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;

/// <summary>WeClapp API access settings — resolved from a tenant GlobalConfiguration entry
/// (e.g. "WeClappApi") or from inline node configuration. Members are deliberately not
/// <c>required</c>: a half-configured tenant entry must reach the resolver's clear error
/// instead of failing deserialization.</summary>
public record WeClappConnectionSettings
{
    /// <summary>API base, e.g. "https://{tenant}.weclapp.com/webapp/api/v1".</summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>API token (sent as "AuthenticationToken" header) — never log it.</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>Records synthesize a ToString over all members — keep the key out of it.</summary>
    public override string ToString() => $"WeClappConnectionSettings {{ BaseUrl = {BaseUrl}, ApiKey = *** }}";
}

/// <summary>
/// Shared resolution of the WeClapp API access settings — one validation for the fetch
/// trigger and both write-back nodes (mirror of <see cref="SftpConnectionSettingsResolver"/>).
/// A configured-but-missing or half-configured entry fails loud; there is no silent
/// fallback to a possibly stale inline key.
/// </summary>
public static class WeClappConnectionSettingsResolver
{
    public static WeClappConnectionSettings ResolveWeClappSettings(
        this IGlobalConfiguration globalConfiguration,
        string? apiConfiguration, string? inlineBaseUrl, string? inlineApiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiConfiguration))
        {
            if (!globalConfiguration.IsDefined(apiConfiguration))
            {
                throw new WeClappPipelineExecutionException(
                    $"Global configuration '{apiConfiguration}' is not defined for this pipeline " +
                    "— link the configuration entity to the pipeline (Uses association)");
            }

            // A ConfigurationValue of literal null deserializes to null despite the non-null contract.
            var settings = globalConfiguration.GetValue<WeClappConnectionSettings>(apiConfiguration);
            if (settings is null || string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                throw new WeClappPipelineExecutionException(
                    $"Global configuration '{apiConfiguration}' must provide both 'baseUrl' and 'apiKey'");
            }

            return settings;
        }

        if (string.IsNullOrWhiteSpace(inlineBaseUrl) || string.IsNullOrWhiteSpace(inlineApiKey))
        {
            throw new WeClappPipelineExecutionException(
                "WeClapp access is not configured — set 'apiConfiguration' (recommended) " +
                "or inline 'baseUrl' + 'apiKey'");
        }

        return new WeClappConnectionSettings { BaseUrl = inlineBaseUrl, ApiKey = inlineApiKey };
    }
}
