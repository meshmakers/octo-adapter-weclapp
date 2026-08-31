namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests;

/// <summary>
/// Locates a file by its repository-relative path. The suites here assert against the SHIPPED
/// artefacts - the pipeline yamls, CLAUDE.md, the DILOS fixtures - rather than against copies in
/// the build output, so they read them where they actually live by walking up from the test
/// assembly until the path resolves.
/// </summary>
internal static class RepoFiles
{
    internal static string Find(string relativePath)
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
