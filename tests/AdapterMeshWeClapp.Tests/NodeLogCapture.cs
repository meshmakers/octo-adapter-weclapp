using FakeItEasy.Core;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// Formats a captured INodeContext log call: node logs are message templates with args
/// ("... {0} ...") — smoke-test output renders them so the live logs stay readable.
/// </summary>
internal static class NodeLogCapture
{
    public static string Format(IFakeObjectCall call)
    {
        var template = call.Arguments.Get<string>(0) ?? "";
        var args = call.Arguments.Get<object[]>(1);
        return args is { Length: > 0 } ? string.Format(template, args) : template;
    }
}
