using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Astra.Api.Evidence;

/// <summary>
/// An Integration Services package (<c>.dtsx</c>). The package is opened two levels down so each
/// connection manager, variable and task is a chunk of its own (<c>xpath:/Executable/Executables/Executable[2]</c>),
/// and what it says becomes facts: the files its connections point at and the data flow reads or writes,
/// the tables its sources and destinations name, the procedures and tables an Execute SQL task touches,
/// and the configuration keys a Script Task refers to by name. Nothing here runs or evaluates anything:
/// a connection whose string is an expression is reported as the expression unless a variable gives its value.
/// </summary>
public sealed class SsisAdapter : IEvidenceAdapter
{
    private static readonly XmlElementAdapter Chunker = new(alwaysOpen: (_, depth) => depth <= 1, keepApart: null);

    private static readonly Regex ConfigKey = new(@"[""'](?<k>[A-Za-z_]\w*(?::[A-Za-z_]\w*)+)[""']", RegexOptions.Compiled);
    private static readonly Regex VariableReference = new(@"@\[(?:User::|System::)?(?<v>[^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex Server = new(@"(?:Data Source|Server)\s*=\s*(?<s>[^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Database = new(@"(?:Initial Catalog|Database)\s*=\s*(?<d>[^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const int MaxListed = 60;

    private sealed record Connection(string Name, string Type, string? Target, string? Path);

    public AdapterResult Build(AdapterInput input)
    {
        var chunked = Chunker.Build(input);
        if (input.Text is null || chunked.Chunks.Count == 0 || !chunked.Chunks[0].Locator.StartsWith("xpath:", StringComparison.Ordinal))
            return chunked;

        XDocument doc;
        try
        {
            doc = XmlFacts.Load(input.Text);
        }
        catch (XmlException)
        {
            return chunked;
        }

        var root = doc.Root!;
        var warnings = chunked.Warnings.ToList();
        if (!XmlFacts.Is(root, "Executable"))
        {
            warnings.Add($"This does not look like an Integration Services package (an Executable element was expected, found {XmlFacts.Local(root)}), so nothing was read from it; it is chunked as plain XML.");
            return new AdapterResult(chunked.Chunks, warnings, FactSet.Empty);
        }

        string? At(XElement e) => XmlFacts.ChunkFor(chunked.Chunks, XmlFacts.XPathOf(e));

        var file = input.Path[(input.Path.LastIndexOf('/') + 1)..];
        var name = XmlFacts.Attr(root, "ObjectName") ?? ScriptCommandReader.PackageName(file);
        var me = new EntityRefFact(EntityVocabulary.Package, name);
        var entities = new List<EntityFact>();
        var links = new List<LinkFact>();

        entities.Add(new EntityFact(EntityVocabulary.Package, name, At(root), Attrs.Of(
            ("description", XmlFacts.Attr(root, "Description")),
            ("file", input.Path))));

        var variables = ReadVariables(root, name, At, entities);
        var connections = ReadConnections(root, name, variables, At, entities, links, me);
        var tasks = ReadTasks(root, name, connections, At, entities, links, me, out var taskCount);

        var summary = $"Read 1 package with {connections.Count} connection{(connections.Count == 1 ? "" : "s")} and {taskCount} task{(taskCount == 1 ? "" : "s")}.";
        return new AdapterResult(chunked.Chunks, warnings, new FactSet(entities, links), summary);
    }

    // ── variables ───────────────────────────────────────────────────────

    private static Dictionary<string, string> ReadVariables(XElement root, string package, Func<XElement, string?> at, List<EntityFact> entities)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in root.Descendants().Where(e => XmlFacts.Is(e, "Variable")).Take(MaxListed))
        {
            var vName = XmlFacts.Attr(v, "ObjectName");
            if (vName is null) continue;
            var value = v.Elements().Where(e => XmlFacts.Is(e, "VariableValue")).Select(XmlFacts.Text).FirstOrDefault(t => t is not null);
            values[vName] = value ?? "";
            entities.Add(new EntityFact(EntityVocabulary.Package, package, at(v),
                Attrs.Of(("variables", new[] { new { name = vName, value = AdapterText.Clip(value ?? "", 300) } }))));
        }
        return values;
    }

    // ── connections ─────────────────────────────────────────────────────

    private static Dictionary<string, Connection> ReadConnections(
        XElement root, string package, Dictionary<string, string> variables, Func<XElement, string?> at,
        List<EntityFact> entities, List<LinkFact> links, EntityRefFact me)
    {
        var result = new Dictionary<string, Connection>(StringComparer.OrdinalIgnoreCase);
        var managers = root.Descendants()
            .Where(e => XmlFacts.Is(e, "ConnectionManager") && e.Parent is { } p && XmlFacts.Is(p, "ConnectionManagers"))
            .Take(MaxListed);

        foreach (var cm in managers)
        {
            var cName = XmlFacts.Attr(cm, "ObjectName");
            if (cName is null) continue;
            var type = (XmlFacts.Attr(cm, "CreationName") ?? "").ToUpperInvariant();
            var raw = cm.DescendantsAndSelf().Select(e => XmlFacts.Attr(e, "ConnectionString")).FirstOrDefault(s => s is not null);

            // A connection whose string is set by an expression takes its value from a variable.
            var expression = cm.Descendants().Where(e => XmlFacts.Is(e, "PropertyExpression") && (XmlFacts.Attr(e, "Name") ?? "").Equals("ConnectionString", StringComparison.OrdinalIgnoreCase))
                .Select(XmlFacts.Text).FirstOrDefault(t => t is not null);
            if (raw is null && expression is not null)
            {
                var v = VariableReference.Match(expression);
                raw = v.Success && variables.TryGetValue(v.Groups["v"].Value, out var value) && value.Length > 0 ? value : "=" + expression;
            }

            var isFile = type is "FLATFILE" or "FILE" or "EXCEL" || type.StartsWith("FLAT", StringComparison.Ordinal);
            string? path = isFile && raw is not null && !raw.StartsWith('=') ? raw : null;
            var target = isFile ? raw : DatabaseTarget(raw);
            var connection = new Connection(cName, type, target, path);
            result[cName] = connection;

            entities.Add(new EntityFact(EntityVocabulary.Package, package, at(cm),
                Attrs.Of(("connections", new[] { new { name = cName, type = type.ToLowerInvariant(), target } }))));
        }
        return result;
    }

    private static string? DatabaseTarget(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        var server = Server.Match(connectionString);
        var db = Database.Match(connectionString);
        if (!server.Success && !db.Success) return null;
        return server.Success && db.Success ? $"{server.Groups["s"].Value.Trim()}/{db.Groups["d"].Value.Trim()}"
            : (server.Success ? server.Groups["s"].Value : db.Groups["d"].Value).Trim();
    }

    // ── tasks ───────────────────────────────────────────────────────────

    private static List<string> ReadTasks(
        XElement root, string package, Dictionary<string, Connection> connections, Func<XElement, string?> at,
        List<EntityFact> entities, List<LinkFact> links, EntityRefFact me, out int count)
    {
        var tasks = new List<string>();
        var executables = root.Descendants()
            .Where(e => XmlFacts.Is(e, "Executable") && e != root && e.Parent is { } p && XmlFacts.Is(p, "Executables"))
            .Take(MaxListed)
            .ToList();
        count = executables.Count;

        foreach (var task in executables)
        {
            var tName = XmlFacts.Attr(task, "ObjectName") ?? "(unnamed)";
            var creation = XmlFacts.Attr(task, "CreationName") ?? "";
            var kind = TaskKind(creation);
            tasks.Add(tName);
            var locator = at(task);
            entities.Add(new EntityFact(EntityVocabulary.Package, package, locator,
                Attrs.Of(("tasks", new[] { new { name = tName, type = kind } }))));

            switch (kind)
            {
                case "Execute SQL":
                    ReadSqlTask(task, locator, links, me);
                    break;
                case "Data flow":
                    ReadDataFlow(task, locator, connections, links, me);
                    break;
                case "Script":
                    ReadScriptTask(task, tName, locator, links, me);
                    break;
            }
        }
        return tasks;
    }

    private static string TaskKind(string creationName)
    {
        var c = creationName.ToLowerInvariant();
        if (c.Contains("executesqltask")) return "Execute SQL";
        if (c.Contains("pipeline")) return "Data flow";
        if (c.Contains("scripttask")) return "Script";
        if (c.Contains("sequence")) return "Sequence";
        if (c.Contains("forloop") || c.Contains("foreachloop")) return "Loop";
        if (c.Contains("filesystemtask")) return "File system";
        if (c.Contains("sendmailtask")) return "Send mail";
        return string.IsNullOrWhiteSpace(creationName) ? "Task" : creationName.Split('.').Last();
    }

    private static void ReadSqlTask(XElement task, string? locator, List<LinkFact> links, EntityRefFact me)
    {
        var sql = task.DescendantsAndSelf().SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName.Equals("SqlStatementSource", StringComparison.OrdinalIgnoreCase)).Select(a => a.Value).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(sql)) return;

        foreach (var p in ScriptCommandReader.Read(sql).Procedures)
            links.Add(new LinkFact(EntityVocabulary.Runs, me, new(EntityVocabulary.Procedure, p.Name), locator, EntityVocabulary.High, Attrs.Of(("arguments", p.Arguments))));
        var scanned = SqlPatternReader.ScanBody(sql);
        foreach (var t in scanned.Reads)
            links.Add(new LinkFact(EntityVocabulary.Reads, me, new(EntityVocabulary.Table, t), locator, EntityVocabulary.Medium));
        foreach (var t in scanned.Writes)
            links.Add(new LinkFact(EntityVocabulary.Writes, me, new(EntityVocabulary.Table, t), locator, EntityVocabulary.Medium));
    }

    private static void ReadDataFlow(XElement task, string? locator, Dictionary<string, Connection> connections, List<LinkFact> links, EntityRefFact me)
    {
        foreach (var component in task.Descendants().Where(e => XmlFacts.Is(e, "component")))
        {
            var cls = (XmlFacts.Attr(component, "componentClassID") ?? "").ToLowerInvariant();
            var isSource = cls.Contains("source");
            var isDestination = cls.Contains("destination");
            if (!isSource && !isDestination) continue;

            var relation = isSource ? EntityVocabulary.Reads : EntityVocabulary.Writes;
            var viaFile = cls.Contains("flatfile") || cls.Contains("excel");

            if (viaFile)
            {
                var path = ConnectionOf(component, connections)?.Path;
                if (path is not null)
                    links.Add(new LinkFact(relation, me, new(EntityVocabulary.File, path), locator, EntityVocabulary.High,
                        Attrs.Of(("component", XmlFacts.Attr(component, "name")))));
                continue;
            }

            string? Property(params string[] names) => component.Descendants().Where(e => XmlFacts.Is(e, "property") && names.Any(n => (XmlFacts.Attr(e, "name") ?? "").Equals(n, StringComparison.OrdinalIgnoreCase)))
                .Select(XmlFacts.Text).FirstOrDefault(t => t is not null);

            var table = Property("OpenRowset", "TableOrViewName");
            if (table is not null)
            {
                links.Add(new LinkFact(relation, me, new(EntityVocabulary.Table, SqlPatternReader.Normalise(table)), locator, EntityVocabulary.High,
                    Attrs.Of(("component", XmlFacts.Attr(component, "name")))));
            }
            else if (Property("SqlCommand") is { } command)
            {
                foreach (var t in SqlPatternReader.ScanBody(command).Reads)
                    links.Add(new LinkFact(EntityVocabulary.Reads, me, new(EntityVocabulary.Table, t), locator, EntityVocabulary.Medium));
            }
        }
    }

    private static Connection? ConnectionOf(XElement component, Dictionary<string, Connection> connections)
    {
        foreach (var c in component.Descendants().Where(e => XmlFacts.Is(e, "connection")))
        {
            var reference = XmlFacts.Attr(c, "connectionManagerRefId", "connectionManagerID", "connectionManagerName");
            if (reference is null) continue;
            // "Package.ConnectionManagers[VaultFile]" names VaultFile; a bare name is the name.
            var open = reference.LastIndexOf('[');
            var close = reference.LastIndexOf(']');
            var name = open >= 0 && close > open ? reference[(open + 1)..close] : reference;
            if (connections.TryGetValue(name, out var connection)) return connection;
        }
        return null;
    }

    private static void ReadScriptTask(XElement task, string taskName, string? locator, List<LinkFact> links, EntityRefFact me)
    {
        var source = string.Join('\n', task.Descendants().Where(e => XmlFacts.Is(e, "ProjectItem", "ScriptCode", "Source")).Select(e => e.Value));
        foreach (var key in ConfigKey.Matches(source).Select(m => m.Groups["k"].Value).Distinct(StringComparer.Ordinal))
        {
            // A string that looks like a configuration key is a pattern match, not a call to a configuration API.
            links.Add(new LinkFact(EntityVocabulary.Configures, new(EntityVocabulary.ConfigKey, key), me, locator, EntityVocabulary.Medium,
                Attrs.Of(("task", taskName))));
        }
    }
}
