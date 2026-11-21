namespace Astra.Api.Tests;

/// <summary>Finds the API's source folder from where the tests run, for tests that read content that ships with it (a pack) or check how the code is written.</summary>
internal static class SourceTree
{
    /// <summary>The folder holding <c>Astra.Api.csproj</c> (<c>…/src/Astra.Api</c>).</summary>
    public static string ApiRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Astra.Api");
            if (File.Exists(Path.Combine(candidate, "Astra.Api.csproj"))) return candidate;
        }
        throw new DirectoryNotFoundException("Could not find src/Astra.Api above " + AppContext.BaseDirectory);
    }
}
