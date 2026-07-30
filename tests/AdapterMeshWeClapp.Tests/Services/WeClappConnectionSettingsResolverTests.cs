using FakeItEasy;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Services;

/// <summary>
/// Shared WeClapp access resolution (fetch trigger + AR/BE write-back): a tenant
/// GlobalConfiguration entry (e.g. "WeClappApi") wins over inline baseUrl/apiKey;
/// a configured-but-missing or half-configured entry fails loud instead of silently
/// falling back to a possibly stale inline key.
/// </summary>
public class WeClappConnectionSettingsResolverTests
{
    private readonly IGlobalConfiguration _globalConfiguration = A.Fake<IGlobalConfiguration>();

    [Fact]
    public void Resolve_ConfigurationEntry_ReturnsSettingsFromGlobalConfiguration()
    {
        A.CallTo(() => _globalConfiguration.IsDefined("WeClappApi")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<WeClappConnectionSettings>("WeClappApi"))
            .Returns(new WeClappConnectionSettings { BaseUrl = "https://cfg.weclapp.com/webapp/api/v1", ApiKey = "cfg-key" });

        var settings = _globalConfiguration.ResolveWeClappSettings("WeClappApi", null, null);

        Assert.Equal("https://cfg.weclapp.com/webapp/api/v1", settings.BaseUrl);
        Assert.Equal("cfg-key", settings.ApiKey);
    }

    [Fact]
    public void Resolve_ConfigurationWinsOverInline()
    {
        A.CallTo(() => _globalConfiguration.IsDefined("WeClappApi")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<WeClappConnectionSettings>("WeClappApi"))
            .Returns(new WeClappConnectionSettings { BaseUrl = "https://cfg.weclapp.com/webapp/api/v1", ApiKey = "cfg-key" });

        var settings = _globalConfiguration.ResolveWeClappSettings("WeClappApi", "https://inline.example", "inline-key");

        Assert.Equal("cfg-key", settings.ApiKey);
    }

    [Fact]
    public void Resolve_ConfigurationSetButUndefined_FailsLoudWithUsesHint()
    {
        A.CallTo(() => _globalConfiguration.IsDefined("WeClappApi")).Returns(false);

        var ex = Assert.Throws<WeClappPipelineExecutionException>(() =>
            _globalConfiguration.ResolveWeClappSettings("WeClappApi", "https://inline.example", "inline-key"));

        Assert.Contains("WeClappApi", ex.Message);
        Assert.Contains("Uses association", ex.Message);
    }

    [Theory]
    [InlineData("", "cfg-key")]
    [InlineData("https://cfg.weclapp.com/webapp/api/v1", "")]
    public void Resolve_ConfigurationEntryIncomplete_FailsLoud(string baseUrl, string apiKey)
    {
        A.CallTo(() => _globalConfiguration.IsDefined("WeClappApi")).Returns(true);
        A.CallTo(() => _globalConfiguration.GetValue<WeClappConnectionSettings>("WeClappApi"))
            .Returns(new WeClappConnectionSettings { BaseUrl = baseUrl, ApiKey = apiKey });

        var ex = Assert.Throws<WeClappPipelineExecutionException>(() =>
            _globalConfiguration.ResolveWeClappSettings("WeClappApi", null, null));

        Assert.Contains("WeClappApi", ex.Message);
        Assert.Contains("baseUrl", ex.Message);
        Assert.Contains("apiKey", ex.Message);
    }

    [Fact]
    public void Resolve_InlineOnly_ReturnsInlineSettings()
    {
        var settings = _globalConfiguration.ResolveWeClappSettings(null, "https://inline.weclapp.com/webapp/api/v1", "inline-key");

        Assert.Equal("https://inline.weclapp.com/webapp/api/v1", settings.BaseUrl);
        Assert.Equal("inline-key", settings.ApiKey);
        A.CallTo(() => _globalConfiguration.IsDefined(A<string>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData(null, "inline-key")]
    [InlineData("https://inline.weclapp.com/webapp/api/v1", null)]
    [InlineData(null, null)]
    public void Resolve_NoConfigurationAndIncompleteInline_FailsLoud(string? baseUrl, string? apiKey)
    {
        var ex = Assert.Throws<WeClappPipelineExecutionException>(() =>
            _globalConfiguration.ResolveWeClappSettings(null, baseUrl, apiKey));

        Assert.Contains("apiConfiguration", ex.Message);
    }
}
