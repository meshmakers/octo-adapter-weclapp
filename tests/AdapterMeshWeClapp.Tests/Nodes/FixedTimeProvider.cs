namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

/// <summary>Fixed clock for deterministic file-name and time-dependent assertions.</summary>
public sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
