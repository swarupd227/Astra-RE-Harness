using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Astra.Api.Llm.Prompts;

/// <summary>
/// Externalised prompt asset library (Phase #3b).
///
/// Loads markdown prompt files from
/// <c>Llm/Prompts/&lt;sourceSchema&gt;/&lt;targetStack&gt;/&lt;kind&gt;.v&lt;N&gt;.md</c>.
///
/// Each file has YAML-ish frontmatter (key: value lines, no nested objects
/// except trivial list items) followed by alternating <c># System</c> and
/// <c># User</c> sections. Template variables use <c>{{name}}</c> syntax —
/// callers pass a dictionary, the library substitutes verbatim.
///
/// This is the core IP-as-assets layer Nous ships. Customers see a
/// directory tree of prompts they can fork rather than a giant
/// hard-coded string inside the binary.
/// </summary>
public sealed class PromptLibrary
{
    private static readonly Regex FrontmatterSplit =
        new(@"\A---\s*\r?\n(?<body>.*?)\r?\n---\s*\r?\n(?<rest>.*)\z",
            RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex SectionHeader =
        new(@"^#\s+(?<name>System|User)\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<Key, LoadedPrompt> _byKey = new();
    private readonly ConcurrentDictionary<(string Source, string Target, string Kind), LoadedPrompt> _latestByTriple = new();
    private readonly ILogger<PromptLibrary> _log;
    private readonly string _dir;
    public string BaseDir => _dir;

    public PromptLibrary(IHostEnvironment env, IConfiguration cfg, ILogger<PromptLibrary> log)
    {
        _log = log;
        _dir = cfg["Llm:PromptDir"] ?? Path.Combine(AppContext.BaseDirectory, "Llm", "Prompts");
        var dir = _dir;
        if (!Directory.Exists(dir))
        {
            log.LogWarning("No prompt directory at {Dir}; PromptLibrary will be empty", dir);
            return;
        }

        // Walk <dir>/<sourceSchema>/<targetStack>/<kind>.v<N>.md
        foreach (var sourceDir in Directory.EnumerateDirectories(dir))
        {
            var sourceSchema = Path.GetFileName(sourceDir);
            foreach (var targetDir in Directory.EnumerateDirectories(sourceDir))
            {
                var targetStack = Path.GetFileName(targetDir);
                foreach (var file in Directory.EnumerateFiles(targetDir, "*.md"))
                {
                    try
                    {
                        var loaded = LoadFile(file, sourceSchema, targetStack);
                        var key = new Key(sourceSchema, targetStack, loaded.Kind, loaded.Version);
                        _byKey[key] = loaded;

                        // Track the latest version per (source, target, kind) using a
                        // simple lexicographic order on the version string after stripping
                        // a leading 'v'. Good enough for v1.0, v3.2, v0.1 etc.
                        var triple = (sourceSchema, targetStack, loaded.Kind);
                        if (!_latestByTriple.TryGetValue(triple, out var existing)
                            || string.Compare(NormaliseVersion(loaded.Version), NormaliseVersion(existing.Version), StringComparison.OrdinalIgnoreCase) > 0)
                        {
                            _latestByTriple[triple] = loaded;
                        }
                        log.LogInformation(
                            "Loaded prompt {Source}/{Target}/{Kind}@{Version} from {File}",
                            sourceSchema, targetStack, loaded.Kind, loaded.Version, Path.GetFileName(file));
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "Failed to load prompt at {File}", file);
                    }
                }
            }
        }

        if (_byKey.IsEmpty) log.LogWarning("PromptLibrary loaded zero prompts from {Dir}", dir);
    }

    // ────────────────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Every loaded prompt, ordered (source, target, kind, version).</summary>
    public IReadOnlyList<LoadedPrompt> All() => _byKey.Values
        .OrderBy(p => p.SourceSchema)
        .ThenBy(p => p.TargetStack)
        .ThenBy(p => p.Kind)
        .ThenBy(p => p.Version)
        .ToArray();

    /// <summary>Get an exact (source, target, kind, version) match.</summary>
    public LoadedPrompt? Get(string sourceSchema, string targetStack, string kind, string version) =>
        _byKey.TryGetValue(new(sourceSchema, targetStack, kind, version), out var p) ? p : null;

    /// <summary>
    /// Get the latest version of (source, target, kind). Used by the
    /// extraction pipeline when no version is pinned explicitly.
    /// </summary>
    public LoadedPrompt? GetLatest(string sourceSchema, string targetStack, string kind) =>
        _latestByTriple.TryGetValue((sourceSchema, targetStack, kind), out var p) ? p : null;

    // ────────────────────────────────────────────────────────────────────
    // Admin CRUD — Phase #4.3
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Write a prompt file to disk and load it into memory. Throws on
    /// directory-traversal attempts or malformed bodies.
    /// </summary>
    public LoadedPrompt SaveAndLoad(
        string sourceSchema, string targetStack, string kind, string version,
        string markdown, bool overwriteExisting)
    {
        var safeSource = SafeSegment(sourceSchema, nameof(sourceSchema));
        var safeTarget = SafeSegment(targetStack, nameof(targetStack));
        var safeKind = SafeSegment(kind, nameof(kind));
        var safeVersion = SafeSegment(version, nameof(version));

        var targetDir = Path.Combine(_dir, safeSource, safeTarget);
        Directory.CreateDirectory(targetDir);
        var file = Path.Combine(targetDir, $"{safeKind}.v{safeVersion}.md");

        if (!overwriteExisting && File.Exists(file))
            throw new InvalidOperationException($"Prompt file already exists at {file}. Use PUT to overwrite.");

        // Parse-validate FIRST so we don't write a broken file.
        var loaded = ParseLoaded(file, safeSource, safeTarget, markdown);
        // Override version + kind from path so frontmatter typos don't drift.
        var pinned = new LoadedPrompt
        {
            Path = file,
            SourceSchema = loaded.SourceSchema,
            TargetStack = loaded.TargetStack,
            Kind = safeKind,
            Version = safeVersion,
            PromptId = loaded.PromptId,
            SystemTemplate = loaded.SystemTemplate,
            UserTemplate = loaded.UserTemplate,
            Frontmatter = loaded.Frontmatter,
        };

        File.WriteAllText(file, markdown);
        IndexPrompt(pinned);
        _log.LogInformation("Saved prompt {Source}/{Target}/{Kind}@{Version} to {File}",
            safeSource, safeTarget, safeKind, safeVersion, file);
        return pinned;
    }

    /// <summary>Remove a prompt file + drop it from the in-memory index.</summary>
    public bool DeletePrompt(string sourceSchema, string targetStack, string kind, string version)
    {
        var safeSource = SafeSegment(sourceSchema, nameof(sourceSchema));
        var safeTarget = SafeSegment(targetStack, nameof(targetStack));
        var safeKind = SafeSegment(kind, nameof(kind));
        var safeVersion = SafeSegment(version, nameof(version));

        var file = Path.Combine(_dir, safeSource, safeTarget, $"{safeKind}.v{safeVersion}.md");
        var key = new Key(safeSource, safeTarget, safeKind, safeVersion);
        var existed = _byKey.TryRemove(key, out _);

        if (File.Exists(file)) File.Delete(file);

        // Recompute the latest-by-triple entry for this (source, target, kind).
        var triple = (safeSource, safeTarget, safeKind);
        var remaining = _byKey.Values
            .Where(p => p.SourceSchema == safeSource && p.TargetStack == safeTarget && p.Kind == safeKind)
            .OrderByDescending(p => NormaliseVersion(p.Version), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (remaining is not null) _latestByTriple[triple] = remaining;
        else _latestByTriple.TryRemove(triple, out _);

        _log.LogInformation("Deleted prompt {Source}/{Target}/{Kind}@{Version}",
            safeSource, safeTarget, safeKind, safeVersion);
        return existed;
    }

    private void IndexPrompt(LoadedPrompt prompt)
    {
        var key = new Key(prompt.SourceSchema, prompt.TargetStack, prompt.Kind, prompt.Version);
        _byKey[key] = prompt;
        var triple = (prompt.SourceSchema, prompt.TargetStack, prompt.Kind);
        if (!_latestByTriple.TryGetValue(triple, out var existing)
            || string.Compare(NormaliseVersion(prompt.Version), NormaliseVersion(existing.Version), StringComparison.OrdinalIgnoreCase) > 0)
        {
            _latestByTriple[triple] = prompt;
        }
    }

    private static LoadedPrompt ParseLoaded(string path, string sourceSchema, string targetStack, string text)
    {
        var m = FrontmatterSplit.Match(text);
        if (!m.Success)
            throw new InvalidOperationException("Prompt markdown is missing the --- frontmatter --- block.");
        var frontmatter = ParseFrontmatter(m.Groups["body"].Value);
        var body = m.Groups["rest"].Value;
        var (system, user) = SplitSections(body, path);
        var kind = frontmatter.GetValueOrDefault("kind") ?? GuessKindFromFilename(path);
        var version = frontmatter.GetValueOrDefault("version") ?? GuessVersionFromFilename(path);
        var id = frontmatter.GetValueOrDefault("id") ?? $"{sourceSchema}-{kind}";
        return new LoadedPrompt
        {
            Path = path,
            SourceSchema = sourceSchema,
            TargetStack = targetStack,
            Kind = kind,
            Version = version,
            PromptId = id,
            SystemTemplate = system,
            UserTemplate = user,
            Frontmatter = frontmatter,
        };
    }

    /// <summary>
    /// Defensive path-segment validation: reject empty, slashes, ".."
    /// or anything that would let a caller escape the prompts dir.
    /// </summary>
    private static string SafeSegment(string s, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(s))
            throw new ArgumentException($"{fieldName} is required.", fieldName);
        var trimmed = s.Trim();
        if (trimmed.Length > 64)
            throw new ArgumentException($"{fieldName} is too long (max 64 chars).", fieldName);
        foreach (var c in trimmed)
        {
            if (!(char.IsLetterOrDigit(c) || c is '-' || c is '_' || c is '.'))
                throw new ArgumentException($"{fieldName} '{trimmed}' contains an invalid character. Allowed: A-Z a-z 0-9 . _ -", fieldName);
        }
        if (trimmed is "." or "..")
            throw new ArgumentException($"{fieldName} cannot be '.' or '..'.", fieldName);
        return trimmed;
    }

    /// <summary>
    /// Render system + user with template variables substituted. Variables
    /// not referenced in the template are silently ignored.
    /// </summary>
    public Built Render(LoadedPrompt prompt, IReadOnlyDictionary<string, string?> variables)
    {
        return new(
            System: Substitute(prompt.SystemTemplate, variables),
            User: Substitute(prompt.UserTemplate, variables),
            PromptId: prompt.PromptId,
            Version: prompt.Version);
    }

    // ────────────────────────────────────────────────────────────────────
    // Loader
    // ────────────────────────────────────────────────────────────────────

    private static LoadedPrompt LoadFile(string path, string sourceSchema, string targetStack)
    {
        var text = File.ReadAllText(path);
        var m = FrontmatterSplit.Match(text);
        if (!m.Success)
            throw new InvalidOperationException($"Prompt {path} is missing the --- frontmatter --- block.");

        var frontmatter = ParseFrontmatter(m.Groups["body"].Value);
        var body = m.Groups["rest"].Value;
        var (system, user) = SplitSections(body, path);

        // Kind + version come from frontmatter (authoritative); the filename
        // is informational. Filename example: extract.v3.2.md
        var kind = frontmatter.GetValueOrDefault("kind") ?? GuessKindFromFilename(path);
        var version = frontmatter.GetValueOrDefault("version") ?? GuessVersionFromFilename(path);
        var id = frontmatter.GetValueOrDefault("id") ?? $"{sourceSchema}-{kind}";

        return new LoadedPrompt
        {
            Path = path,
            SourceSchema = sourceSchema,
            TargetStack = targetStack,
            Kind = kind,
            Version = version,
            PromptId = id,
            SystemTemplate = system,
            UserTemplate = user,
            Frontmatter = frontmatter,
        };
    }

    private static Dictionary<string, string> ParseFrontmatter(string body)
    {
        // Trivial parser: each line is "key: value", with `notes: |` and
        // list items indented. We surface scalar keys flat and ignore
        // nested structures (the model bears the cost of stringly-typed
        // metadata — pre-Phase D we don't need richer types).
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? lastKey = null;
        var pipe = new System.Text.StringBuilder();
        bool inPipe = false;
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (inPipe)
            {
                if (line.StartsWith("  ")) { pipe.AppendLine(line[2..]); continue; }
                if (line.StartsWith(" ")) { pipe.AppendLine(line.TrimStart()); continue; }
                if (line.Length == 0) { pipe.AppendLine(); continue; }
                // Otherwise the pipe block ended.
                if (lastKey != null) dict[lastKey] = pipe.ToString().TrimEnd();
                inPipe = false;
                pipe.Clear();
            }
            if (line.Length == 0) continue;
            if (line.StartsWith("  - ")) continue;  // list item under the previous key; ignore
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var k = line[..idx].Trim();
            var v = line[(idx + 1)..].Trim();
            if (v == "|") { lastKey = k; inPipe = true; pipe.Clear(); continue; }
            dict[k] = v;
            lastKey = k;
        }
        if (inPipe && lastKey != null) dict[lastKey] = pipe.ToString().TrimEnd();
        return dict;
    }

    private static (string System, string User) SplitSections(string body, string path)
    {
        var matches = SectionHeader.Matches(body);
        if (matches.Count == 0)
            throw new InvalidOperationException($"Prompt {path} is missing # System / # User headers.");
        string system = "", user = "";
        for (int i = 0; i < matches.Count; i++)
        {
            var name = matches[i].Groups["name"].Value;
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : body.Length;
            var section = body[start..end].Trim('\r', '\n');
            if (name.Equals("System", StringComparison.OrdinalIgnoreCase)) system = section;
            else if (name.Equals("User", StringComparison.OrdinalIgnoreCase)) user = section;
        }
        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(user))
            throw new InvalidOperationException($"Prompt {path} must define both # System and # User sections.");
        return (system, user);
    }

    private static string Substitute(string template, IReadOnlyDictionary<string, string?> vars)
    {
        if (string.IsNullOrEmpty(template)) return template;
        return Regex.Replace(template, @"\{\{\s*(?<key>[A-Za-z0-9_]+)\s*\}\}", m =>
        {
            var key = m.Groups["key"].Value;
            return vars.TryGetValue(key, out var v) && v != null ? v : m.Value;
        });
    }

    private static string GuessKindFromFilename(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static string GuessVersionFromFilename(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var dot = name.IndexOf('.');
        return dot > 0 ? name[(dot + 1)..] : "v0";
    }

    private static string NormaliseVersion(string v)
    {
        // "v3.2" → "003.002"; cheap right-pad-with-zeros so 3.2 sorts after 0.10.
        var trimmed = v.TrimStart('v', 'V');
        var parts = trimmed.Split('.').Select(p => int.TryParse(p, out var n) ? n.ToString("D3") : p);
        return string.Join('.', parts);
    }

    // ────────────────────────────────────────────────────────────────────
    // Models
    // ────────────────────────────────────────────────────────────────────

    private readonly record struct Key(string Source, string Target, string Kind, string Version);

    public sealed class LoadedPrompt
    {
        public string Path { get; init; } = "";
        public string SourceSchema { get; init; } = "";
        public string TargetStack { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Version { get; init; } = "";
        public string PromptId { get; init; } = "";
        public string SystemTemplate { get; init; } = "";
        public string UserTemplate { get; init; } = "";
        public Dictionary<string, string> Frontmatter { get; init; } = new();
    }

    public sealed record Built(string System, string User, string PromptId, string Version);
}
