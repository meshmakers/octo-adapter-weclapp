using System.Reflection;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// Keeps CLAUDE.md's guard-test inventory in lockstep with the guards that actually exist.
/// The inventory had already drifted once: AB#4845 added two contract tests without naming
/// them, so CLAUDE.md listed 5 of 12 while claiming to enumerate what pins the YAML
/// contracts — a reader could reasonably conclude an invariant was unpinned and re-pin it.
/// Same trick as PipelineYamlContractTests' theory-lockstep meta-test, one level up: the
/// documentation is checked against the code instead of against someone's memory.
/// </summary>
public class DocumentationContractTests
{
    // Guards that CLAUDE.md deliberately does NOT name. Empty on purpose. Adding an entry
    // is a decision that a guard is too narrow for the project brief, and it has to survive
    // review — never add one merely to make this test green.
    private static readonly string[] UndocumentedByDesign = [];

    [Fact]
    public void ClaudeMd_NamesEveryPipelineContractTest()
    {
        var claudeMd = File.ReadAllText(FindRepoFile("CLAUDE.md"));

        var missing = ContractTestNames()
            .Where(name => !UndocumentedByDesign.Contains(name, StringComparer.Ordinal))
            .Where(name => !claudeMd.Contains(name, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0,
            "CLAUDE.md must name every PipelineYamlContractTests guard, otherwise its "
            + "inventory stops being trustworthy. Not named: " + string.Join(", ", missing));
    }

    [Fact]
    public void UndocumentedByDesign_ListsNoTestThatNoLongerExists()
    {
        var actual = ContractTestNames().ToHashSet(StringComparer.Ordinal);

        var stale = UndocumentedByDesign
            .Where(name => !actual.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(stale.Length == 0,
            "UndocumentedByDesign exempts tests that no longer exist, so the exemption is "
            + "silently covering nothing: " + string.Join(", ", stale));
    }

    private static IEnumerable<string> ContractTestNames()
    {
        return typeof(PipelineYamlContractTests)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes()
                .Any(a => a.GetType().Name is "FactAttribute" or "TheoryAttribute"))
            .Select(m => m.Name);
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"'{relativePath}' not found above {AppContext.BaseDirectory}");
    }
}
