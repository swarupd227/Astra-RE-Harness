using System.Text;
using System.Text.RegularExpressions;
using Astra.Api.Llm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Astra.Api.Assembly;

/// <summary>
/// dotnet10-faithful: many 1:1 unit packages become one runnable .NET 10 application. Each unit's converted files are
/// kept as written (a unit converted for several of its routines is taken once, from its newest package); the
/// compatibility runtime, provenance attributes and project shell are held once; a stub of another unit of the project
/// is dropped when that unit's real conversion is in the application; stubs of libraries whose source is not in the
/// project are merged type by type, member by member, so two units that each stubbed part of a library give one stub
/// with both parts. Each unit's tests move to a namespace of their own. When the units carry Blazor pages the
/// application is a Blazor Server app with a start page listing them. Pure and deterministic: no model is involved.
/// </summary>
public sealed class DotnetFaithfulAssembler : IModuleAssembler
{
    public string TargetStack => FaithfulConversion.Stack;

    private const string StubsUnits = "src/Stubs/Units/";
    private const string Stubs = "src/Stubs/";
    private static readonly string[] Shared = ["src/Provenance.cs", "tests/UnitTests.cs", "tests/Faithful.Unit.Tests.csproj"];

    public AssembledModule Assemble(IReadOnlyList<UnitPackage> units, AssemblyOptions options)
    {
        var notes = new List<string>();
        var shared = new List<SharedItem>();
        var conflicts = new List<AssemblyConflict>();
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        // One package per source unit: the newest (every routine of a unit carries the whole unit).
        var byUnit = units
            .GroupBy(u => u.SourcePath, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(u => u.GeneratedAt ?? DateTimeOffset.MinValue).ThenBy(u => u.ScaffoldId).First())
            .OrderBy(u => u.SourcePath, StringComparer.Ordinal)
            .ToList();
        if (units.Count > byUnit.Count)
            notes.Add($"{units.Count} routine packages came from {byUnit.Count} source units; each unit is taken once, from its newest package.");

        // The classes the application really has (src/<Name>.cs and its parts), so a stub of one of them is dropped.
        var realClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in byUnit)
            foreach (var path in u.Files.Keys)
                if (IsUnitSource(path) && Path.GetFileNameWithoutExtension(path).Split('.')[0] is { Length: > 0 } cls)
                    realClasses.Add(cls);

        var assembled = new List<AssembledUnit>();
        var stubSources = new List<(string Unit, string Path, string Content)>();
        var sharedSeen = new Dictionary<string, (string Content, List<string> Units)>(StringComparer.Ordinal);
        var hasPages = false;

        foreach (var u in byUnit)
        {
            var count = 0;
            var testNs = "Faithful.Tests." + FaithfulConversion.IdentifierOf(FaithfulConversion.UnitNameOf(u.SourcePath));
            foreach (var (rawPath, content) in u.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var path = rawPath.Replace('\\', '/');
                if (FaithfulConversion.IsExemplar(path) || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("tests/", StringComparison.Ordinal))
                    continue;
                if (path.StartsWith("src/Vb6Runtime/", StringComparison.Ordinal) || Shared.Contains(path))
                {
                    if (!sharedSeen.TryGetValue(path, out var s)) sharedSeen[path] = (content, new List<string> { u.UnitName });
                    else
                    {
                        s.Units.Add(u.UnitName);
                        if (s.Content != content)
                            conflicts.Add(new("shared-file", path, "Two units carry different copies; the first is kept.", [s.Units[0], u.UnitName]));
                    }
                    continue;
                }
                if (path.StartsWith(Stubs, StringComparison.Ordinal))
                {
                    if (path.StartsWith(StubsUnits, StringComparison.Ordinal)
                        && realClasses.Contains(Path.GetFileNameWithoutExtension(path).Split('.')[0]))
                        continue;   // the real conversion is in the application
                    stubSources.Add((u.UnitName, path, content));
                    continue;
                }
                if (path.StartsWith("tests/", StringComparison.Ordinal))
                {
                    var name = path["tests/".Length..];
                    files[$"tests/Units/{FaithfulConversion.IdentifierOf(FaithfulConversion.UnitNameOf(u.SourcePath))}/{name}"] =
                        Regex.Replace(content, @"^(\s*)namespace\s+Faithful\.Tests\s*;", $"$1namespace {testNs};", RegexOptions.Multiline);
                    count++;
                    continue;
                }
                if (files.ContainsKey(path))
                {
                    conflicts.Add(new("file", path, "Two units wrote the same file; the first is kept.", [u.UnitName]));
                    continue;
                }
                if (path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)) hasPages = true;
                files[path] = content;
                count++;
            }
            assembled.Add(new AssembledUnit(u.UnitId, u.UnitName, u.SourcePath, u.ScaffoldId, "Faithful.Vb6App", count));
        }

        foreach (var (path, (content, owners)) in sharedSeen)
        {
            files[path] = content;
            if (owners.Count > 1) shared.Add(new SharedItem("file", Path.GetFileName(path), path, owners));
        }

        // Stubs that stay (libraries, units not in this application): one file per type, members merged.
        foreach (var (path, content, owners) in MergeStubs(stubSources, conflicts))
        {
            files[path] = content;
            if (owners.Count > 1) shared.Add(new SharedItem("stub", Path.GetFileNameWithoutExtension(path), path, owners));
        }

        // The project shell: one web app when there are pages, else the library the units already used.
        files["Faithful.App.csproj"] = hasPages ? WebProject(options.ModuleName) : LibraryProject(options.ModuleName);
        if (files.ContainsKey("tests/Faithful.Unit.Tests.csproj"))
        {
            files["tests/Faithful.App.Tests.csproj"] = files["tests/Faithful.Unit.Tests.csproj"].Replace(@"..\Faithful.Unit.csproj", @"..\Faithful.App.csproj");
            files.Remove("tests/Faithful.Unit.Tests.csproj");
        }
        if (hasPages)
        {
            var pages = files.Keys.Where(p => Regex.IsMatch(p, @"^src/View_[^/]+\.razor$")).Select(p => Path.GetFileNameWithoutExtension(p)["View_".Length..]).ToList();
            foreach (var (path, content) in HostFiles(options.ModuleName, pages)) files[path] = content;
            notes.Add($"Blazor Server host added: {pages.Count} form page(s) under /forms/<name>, listed on the start page.");
        }

        var stubTypes = files.Keys.Count(p => p.StartsWith(Stubs, StringComparison.Ordinal));
        if (stubTypes > 0)
            notes.Add($"{stubTypes} stub file(s) remain: code this application calls but whose source is not in it. They throw NotImplementedException until replaced.");

        var report = new AssemblyReport(ModuleAssemblers.Slug(options.ModuleName), "Faithful.Vb6App", hasPages ? "Program" : "(library)",
            assembled, shared, conflicts, notes);
        files["ASSEMBLY.md"] = Describe(options.ModuleName, report);
        return new AssembledModule(files, report);
    }

    /// <summary>A unit's own converted file (src/X.cs, src/X.PartN.cs, src/View_X.razor), not the runtime or a stub.</summary>
    private static bool IsUnitSource(string path) =>
        path.StartsWith("src/", StringComparison.Ordinal) && path.Count(c => c == '/') == 1
        && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && path != "src/Provenance.cs";

    /// <summary>
    /// Merges stub files: every type declared in any stub, keyed by namespace and name, becomes one declaration whose
    /// members are the union of all the stubs' members (a member is the same when its kind, name and parameter count
    /// match). Using directives are unioned. A file that does not parse is kept as it is, under its own path.
    /// </summary>
    internal static IReadOnlyList<(string Path, string Content, List<string> Owners)> MergeStubs(
        IReadOnlyList<(string Unit, string Path, string Content)> stubs, List<AssemblyConflict> conflicts)
    {
        var types = new Dictionary<string, (string Namespace, string Dir, TypeDeclarationSyntax? Type, EnumDeclarationSyntax? Enum, List<MemberDeclarationSyntax> Members, HashSet<string> Keys, SortedSet<string> Usings, List<string> Owners, string Header)>(StringComparer.Ordinal);
        var kept = new List<(string Path, string Content, List<string> Owners)>();

        foreach (var (unit, path, content) in stubs)
        {
            var tree = CSharpSyntaxTree.ParseText(content);
            var root = tree.GetCompilationUnitRoot();
            if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                if (kept.All(k => k.Path != path)) kept.Add((path, content, [unit]));
                conflicts.Add(new("stub", path, "A stub did not parse; it is kept as written.", [unit]));
                continue;
            }
            var usings = root.Usings.Select(x => x.ToString().Trim()).ToList();
            var header = string.Join('\n', content.Replace("\r\n", "\n").Split('\n').TakeWhile(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (var ns in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().DefaultIfEmpty())
            {
                var nsName = ns?.Name.ToString() ?? "";
                var nsUsings = ns?.Usings.Select(x => x.ToString().Trim()) ?? [];
                var members = ns is null ? root.Members : ns.Members;
                foreach (var m in members)
                {
                    if (m is BaseNamespaceDeclarationSyntax) continue;
                    var name = m switch { BaseTypeDeclarationSyntax t => t.Identifier.Text, DelegateDeclarationSyntax d => d.Identifier.Text, _ => "" };
                    if (name.Length == 0) continue;
                    var key = $"{nsName}.{name}";
                    var dir = Path.GetDirectoryName(path)!.Replace('\\', '/');
                    if (!types.TryGetValue(key, out var entry))
                    {
                        entry = (nsName, dir, m as TypeDeclarationSyntax, m as EnumDeclarationSyntax, new List<MemberDeclarationSyntax>(),
                            new HashSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal), new List<string>(), header);
                        types[key] = entry;
                    }
                    foreach (var u in usings.Concat(nsUsings)) entry.Usings.Add(u);
                    if (!entry.Owners.Contains(unit)) entry.Owners.Add(unit);
                    if (m is TypeDeclarationSyntax td)
                        foreach (var member in td.Members)
                            if (entry.Keys.Add(MemberKey(member))) entry.Members.Add(member);
                    if (m is EnumDeclarationSyntax ed)
                        foreach (var v in ed.Members)
                            if (entry.Keys.Add("enum:" + v.Identifier.Text)) entry.Members.Add(SyntaxFactory.ParseMemberDeclaration($"int {v.Identifier.Text};")!);
                }
            }
        }

        var result = new List<(string Path, string Content, List<string> Owners)>(kept);
        foreach (var (key, e) in types.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            var name = key[(key.LastIndexOf('.') + 1)..];
            var sb = new StringBuilder();
            if (e.Header.Length > 0) sb.Append(e.Header).Append('\n');
            sb.Append($"// Assembled from the stubs of: {string.Join(", ", e.Owners)}.\n");
            var globals = e.Usings.Where(u => u.StartsWith("global ", StringComparison.Ordinal)).ToList();
            foreach (var g in globals) sb.Append(g).Append('\n');
            if (e.Namespace.Length > 0) sb.Append($"namespace {e.Namespace};\n");
            foreach (var u in e.Usings.Except(globals)) sb.Append(u).Append('\n');
            sb.Append('\n');
            if (e.Enum is { } en)
            {
                var values = string.Join(",\n", en.Members.Select(v => "    " + v.ToString().Trim()));
                var extra = e.Keys.Where(k => k.StartsWith("enum:", StringComparison.Ordinal)).Select(k => k[5..])
                    .Where(v => en.Members.All(x => x.Identifier.Text != v)).Select(v => "    " + v);
                sb.Append(en.WithMembers(default).AttributeLists.ToFullString())
                  .Append($"{en.Modifiers.ToString()} enum {en.Identifier.Text}{en.BaseList?.ToString() ?? ""}\n{{\n")
                  .Append(string.Join(",\n", new[] { values }.Concat(extra).Where(s => s.Length > 0))).Append("\n}\n");
            }
            else if (e.Type is { } t)
            {
                var decl = t.WithMembers(SyntaxFactory.List(e.Members)).NormalizeWhitespace("    ", "\n");
                sb.Append(decl.ToFullString()).Append('\n');
            }
            result.Add(($"{e.Dir}/{name}.cs", sb.ToString(), e.Owners));
        }
        return result;
    }

    private static string MemberKey(MemberDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax x => $"method:{x.Identifier.Text}/{x.ParameterList.Parameters.Count}",
        ConstructorDeclarationSyntax x => $"ctor/{x.ParameterList.Parameters.Count}",
        PropertyDeclarationSyntax x => $"prop:{x.Identifier.Text}",
        IndexerDeclarationSyntax x => $"indexer/{x.ParameterList.Parameters.Count}",
        FieldDeclarationSyntax x => "field:" + string.Join(",", x.Declaration.Variables.Select(v => v.Identifier.Text)),
        EventFieldDeclarationSyntax x => "event:" + string.Join(",", x.Declaration.Variables.Select(v => v.Identifier.Text)),
        BaseTypeDeclarationSyntax x => $"type:{x.Identifier.Text}",
        DelegateDeclarationSyntax x => $"delegate:{x.Identifier.Text}",
        _ => "other:" + m.ToString().GetHashCode(StringComparison.Ordinal),
    };

    private static string LibraryProject(string name) => $"""
        <Project Sdk="Microsoft.NET.Sdk.Razor">
          <!-- {Escape(name)}: the faithful 1:1 units, assembled. -->
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <RootNamespace>Faithful</RootNamespace>
            <AssemblyName>Faithful.App</AssemblyName>
            <NoWarn>$(NoWarn);CS8600;CS8601;CS8602;CS8603;CS8604;CS8618;CS8625;CS0162;CS0168;CS0219</NoWarn>
          </PropertyGroup>
          <ItemGroup>
            <FrameworkReference Include="Microsoft.AspNetCore.App" />
          </ItemGroup>
          <ItemGroup>
            <Compile Remove="tests/**/*.cs" />
            <Content Remove="tests/**" />
            <None Remove="tests/**" />
          </ItemGroup>
        </Project>
        """;

    private static string WebProject(string name) => $"""
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <!-- {Escape(name)}: the faithful 1:1 units and their form pages, assembled into one Blazor Server app. -->
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <RootNamespace>Faithful</RootNamespace>
            <AssemblyName>Faithful.App</AssemblyName>
            <NoWarn>$(NoWarn);CS8600;CS8601;CS8602;CS8603;CS8604;CS8618;CS8625;CS0162;CS0168;CS0219</NoWarn>
          </PropertyGroup>
          <ItemGroup>
            <Compile Remove="tests/**/*.cs" />
            <Content Remove="tests/**" />
            <None Remove="tests/**" />
          </ItemGroup>
        </Project>
        """;

    private static IEnumerable<(string Path, string Content)> HostFiles(string name, IReadOnlyList<string> forms)
    {
        yield return ("src/Host/Program.cs", """
            // The web host of the assembled application: Blazor Server, one page per converted form.
            using Faithful.Host;

            var builder = WebApplication.CreateBuilder(args);
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            var app = builder.Build();
            // Where the converted code looks for its data files (VB6 App.Path): configurable, the content root by default.
            Faithful.Vb6.App.Path = builder.Configuration["Faithful:DataPath"] ?? app.Environment.ContentRootPath;
            app.UseStaticFiles();
            app.UseAntiforgery();
            app.MapRazorComponents<HostApp>().AddInteractiveServerRenderMode();
            app.Run();
            """);
        yield return ("src/Host/HostApp.razor", $$"""
            @namespace Faithful.Host
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>{{Escape(name)}}</title>
                <base href="/" />
                <style>
                    body { font-family: Segoe UI, Arial, sans-serif; margin: 16px; }
                    .vb-form { border: 1px solid #999; background: #ece9d8; }
                    .vb-grid { border-collapse: collapse; overflow: auto; display: block; background: #fff; }
                    .vb-grid td, .vb-grid th { border: 1px solid #ccc; padding: 2px 4px; font-size: 12px; }
                </style>
                <HeadOutlet @rendermode="InteractiveServer" />
            </head>
            <body>
                <Routes @rendermode="InteractiveServer" />
                <script src="_framework/blazor.web.js"></script>
            </body>
            </html>
            """);
        yield return ("src/Host/Routes.razor", """
            @namespace Faithful.Host
            <Router AppAssembly="typeof(Program).Assembly">
                <Found Context="routeData"><RouteView RouteData="routeData" /></Found>
                <NotFound><p>No such page.</p></NotFound>
            </Router>
            """);
        var links = string.Concat(forms.OrderBy(f => f, StringComparer.Ordinal).Select(f => $"    <li><a href=\"forms/{f}\">{f}</a></li>\n"));
        yield return ("src/Host/Home.razor", $"""
            @page "/"
            @namespace Faithful.Host
            <h1>{Escape(name)}</h1>
            <p>Forms converted 1:1 from VB6. Code this application calls but whose source is not in it is stubbed and reports so.</p>
            <ul>
            {links}</ul>
            """);
        yield return ("src/Host/_Imports.razor", """
            @using Microsoft.AspNetCore.Components.Web
            @using Microsoft.AspNetCore.Components.Routing
            @using static Microsoft.AspNetCore.Components.Web.RenderMode
            """);
    }

    private static string Escape(string s) => s.Replace("<", "").Replace(">", "").Replace("&", "and").Replace("--", "-");

    private static string Describe(string name, AssemblyReport r)
    {
        var sb = new StringBuilder();
        sb.Append($"# {name} — assembled application\n\n");
        sb.Append($"{r.Units.Count} source unit(s) converted 1:1 to .NET 10, in namespace `{r.BasePackage}`.\n\n");
        sb.Append("| Unit | Source | Files |\n|---|---|---|\n");
        foreach (var u in r.Units) sb.Append($"| {u.UnitName} | `{u.SourcePath}` | {u.FileCount} |\n");
        if (r.Shared.Count > 0)
        {
            sb.Append("\n## Held once\n\n");
            foreach (var s in r.Shared) sb.Append($"- {s.Kind} `{s.Location}` — from {string.Join(", ", s.Units)}\n");
        }
        if (r.Conflicts.Count > 0)
        {
            sb.Append("\n## Disagreements\n\n");
            foreach (var c in r.Conflicts) sb.Append($"- {c.Kind} `{c.Name}`: {c.Detail} ({string.Join(", ", c.Units)})\n");
        }
        if (r.Notes.Count > 0)
        {
            sb.Append("\n## Notes\n\n");
            foreach (var n in r.Notes) sb.Append($"- {n}\n");
        }
        return sb.ToString();
    }
}
