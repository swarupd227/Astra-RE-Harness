using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Astra.Api.Evidence;

/// <summary>
/// Scheduler exports in the Automic (UC4) style. A job plan (<c>JOBP</c>) is cut so each task is a chunk
/// of its own, <c>xpath:/JOBP/Tasks/Task[3]</c>, next to one chunk per header element (schedule, host,
/// notification …); a job (<c>JOBS…</c>) is one chunk, or one per element when it is large. What the
/// export says becomes facts: a plan contains its jobs, a task runs after the tasks it names, the task
/// marked as the restart point is the plan's restart point, and what a job's command runs — a stored
/// procedure, a <c>dtexec</c> package, a <c>bcp</c> export, a script — becomes a link to that thing.
/// <para>
/// This is the shape our exports take (see docs/03_Evidence/artefact-adapters.md), read by rules that
/// tolerate the usual variants: a <c>uc-export</c> wrapper, other attribute spellings, header values as
/// attributes or as child elements. A file that is not recognisable as a scheduler export is still
/// chunked as generic XML, with a warning that no jobs were read from it.
/// </para>
/// </summary>
public sealed class JobAdapter : IEvidenceAdapter
{
    private static readonly XmlElementAdapter Chunker = new(
        alwaysOpen: (name, _) => name.Equals("uc-export", StringComparison.OrdinalIgnoreCase)
                                 || name.Equals("JOBP", StringComparison.OrdinalIgnoreCase)
                                 || name.Equals("Tasks", StringComparison.OrdinalIgnoreCase),
        keepApart: name => name.Equals("Task", StringComparison.OrdinalIgnoreCase));

    private const int MaxScriptChars = 2000;

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

        var warnings = chunked.Warnings.ToList();
        var entities = new List<EntityFact>();
        var links = new List<LinkFact>();
        int plans = 0, jobs = 0, tasks = 0;

        var root = doc.Root!;
        var objects = XmlFacts.Is(root, "uc-export") ? root.Elements().ToList() : new List<XElement> { root };
        foreach (var obj in objects)
        {
            if (XmlFacts.Is(obj, "JOBP"))
            {
                if (ReadPlan(obj, chunked.Chunks, entities, links, warnings, out var taskCount)) { plans++; tasks += taskCount; }
            }
            else if (XmlFacts.Local(obj).StartsWith("JOBS", StringComparison.OrdinalIgnoreCase))
            {
                if (ReadJob(obj, chunked.Chunks, entities, links, warnings)) jobs++;
            }
        }

        if (plans + jobs == 0)
            warnings.Add($"This does not look like a scheduler export (a JOBP or JOBS element was expected, found {XmlFacts.Local(root)}), so no jobs were read from it; it is chunked as plain XML.");

        return new AdapterResult(chunked.Chunks, warnings, new FactSet(entities, links), Summary(plans, tasks, jobs));
    }

    // ── a job plan ──────────────────────────────────────────────────────

    private static bool ReadPlan(
        XElement plan, IReadOnlyList<EvidenceChunkDraft> chunks, List<EntityFact> entities, List<LinkFact> links,
        List<string> warnings, out int taskCount)
    {
        taskCount = 0;
        var name = XmlFacts.Attr(plan, "name");
        if (name is null)
        {
            warnings.Add("A job plan has no name, so it was not read.");
            return false;
        }

        string? At(XElement e) => XmlFacts.ChunkFor(chunks, XmlFacts.XPathOf(e));

        entities.Add(new EntityFact(EntityVocabulary.Job, name, At(plan), Attrs.Of(("kind", "jobplan"))));
        ReadHeader(plan, name, chunks, entities, skip: e => XmlFacts.Is(e, "Tasks"));

        var taskElements = plan.Descendants().Where(e => XmlFacts.Is(e, "Task")).ToList();
        var byNumber = new Dictionary<int, string>();
        var rows = new List<(XElement Element, int Number, string Job, List<string> After, bool Restart)>();
        for (var i = 0; i < taskElements.Count; i++)
        {
            var t = taskElements[i];
            var job = XmlFacts.AttrOrChild(t, "Job", "Object", "Name", "Ref");
            if (job is null)
            {
                warnings.Add($"Task {i + 1} of {name} names no job, so it was left out.");
                continue;
            }
            var number = int.TryParse(XmlFacts.Attr(t, "Num", "Lnr", "Number", "Order", "Seq"), out var n) ? n : i + 1;
            var after = (XmlFacts.Attr(t, "After", "Pre", "Predecessor", "Predecessors", "DependsOn") ?? "")
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var restart = XmlFacts.Truthy(XmlFacts.Attr(t, "Restart", "RestartPoint"));
            byNumber[number] = job;
            rows.Add((t, number, job, after, restart));
        }
        taskCount = rows.Count;

        // A restart point can also be stated once in the header: <Restart task="3"/>.
        var headerRestart = plan.Elements().FirstOrDefault(e => XmlFacts.Is(e, "Restart", "RestartPoint"));
        int? headerTask = null;
        string? headerJob = null;
        if (headerRestart is not null)
        {
            var value = XmlFacts.Attr(headerRestart, "task", "num", "step") ?? XmlFacts.Text(headerRestart);
            if (int.TryParse(value, out var rn)) headerTask = rn;
            headerJob = XmlFacts.Attr(headerRestart, "job");
        }

        foreach (var (element, number, job, after, restart) in rows)
        {
            var locator = At(element);
            var marked = restart || headerTask == number || (headerJob is not null && headerJob.Equals(job, StringComparison.OrdinalIgnoreCase));
            links.Add(new LinkFact(EntityVocabulary.Contains, new(EntityVocabulary.Job, name), new(EntityVocabulary.Job, job), locator,
                EntityVocabulary.High, Attrs.Of(("task", number), ("restartPoint", marked ? (object?)true : null))));

            foreach (var token in after)
            {
                var predecessor = int.TryParse(token, out var num) ? byNumber.GetValueOrDefault(num) : token;
                if (predecessor is null)
                {
                    warnings.Add($"Task {number} of {name} runs after task {token}, which is not in the plan.");
                    continue;
                }
                links.Add(new LinkFact(EntityVocabulary.Precedes, new(EntityVocabulary.Job, predecessor), new(EntityVocabulary.Job, job), locator,
                    EntityVocabulary.High, Attrs.Of(("plan", name), ("task", number))));
            }

            if (marked)
            {
                // The restart point is a fact about the plan, shown by the task that carries the mark (or the header that states it).
                entities.Add(new EntityFact(EntityVocabulary.Job, name, restart ? locator : At(headerRestart!),
                    Attrs.Of(("restartTask", number), ("restartJob", job))));
            }
        }

        var tasksElement = plan.Elements().FirstOrDefault(e => XmlFacts.Is(e, "Tasks"));
        entities.Add(new EntityFact(EntityVocabulary.Job, name, tasksElement is null ? At(plan) : At(tasksElement),
            Attrs.Of(("tasks", rows.Select(r => new { number = r.Number, job = r.Job }).ToList()), ("taskCount", rows.Count))));
        return true;
    }

    // ── a job ───────────────────────────────────────────────────────────

    private static bool ReadJob(
        XElement job, IReadOnlyList<EvidenceChunkDraft> chunks, List<EntityFact> entities, List<LinkFact> links, List<string> warnings)
    {
        var name = XmlFacts.Attr(job, "name");
        if (name is null)
        {
            warnings.Add($"A {XmlFacts.Local(job)} element has no name, so it was not read.");
            return false;
        }

        string? At(XElement e) => XmlFacts.ChunkFor(chunks, XmlFacts.XPathOf(e));

        var scriptElement = job.Descendants().FirstOrDefault(e => XmlFacts.Is(e, "Script", "MSCRI", "Command", "Cmd", "CommandLine", "CmdLine"));
        var script = scriptElement is null ? null : XmlFacts.Text(scriptElement);
        var firstCommand = script?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("::") && !l.StartsWith('#'));

        entities.Add(new EntityFact(EntityVocabulary.Job, name, At(job), Attrs.Of(
            ("kind", "job"),
            ("jobType", XmlFacts.Local(job)))));
        ReadHeader(job, name, chunks, entities, skip: e => scriptElement is not null && e == scriptElement);
        if (script is not null)
        {
            entities.Add(new EntityFact(EntityVocabulary.Job, name, At(scriptElement!), Attrs.Of(
                ("command", firstCommand is null ? null : AdapterText.Clip(firstCommand, 300)),
                ("script", AdapterText.Clip(script, MaxScriptChars)))));
        }

        var scriptLocator = scriptElement is null ? At(job) : At(scriptElement);
        var commands = ScriptCommandReader.Read(script);
        var me = new EntityRefFact(EntityVocabulary.Job, name);
        foreach (var p in commands.Procedures)
            links.Add(new LinkFact(EntityVocabulary.Runs, me, new(EntityVocabulary.Procedure, p.Name), scriptLocator, EntityVocabulary.High, Attrs.Of(("arguments", p.Arguments))));
        foreach (var p in commands.Packages)
            links.Add(new LinkFact(EntityVocabulary.Runs, me, new(EntityVocabulary.Package, p.Name), scriptLocator, EntityVocabulary.High, Attrs.Of(("path", p.Path))));
        foreach (var s in commands.Scripts)
            links.Add(new LinkFact(EntityVocabulary.Runs, me, new(EntityVocabulary.Script, s), scriptLocator, EntityVocabulary.High));
        foreach (var f in commands.FilesWritten)
            links.Add(new LinkFact(EntityVocabulary.Writes, me, new(EntityVocabulary.File, f.Path), scriptLocator, EntityVocabulary.High, Attrs.Of(("tool", f.Tool))));
        foreach (var f in commands.FilesRead)
            links.Add(new LinkFact(EntityVocabulary.Reads, me, new(EntityVocabulary.File, f.Path), scriptLocator, EntityVocabulary.High, Attrs.Of(("tool", f.Tool))));
        // Tables named inside a query on the command line are found by pattern, so they are not as firm as a command.
        foreach (var t in commands.TablesRead)
            links.Add(new LinkFact(EntityVocabulary.Reads, me, new(EntityVocabulary.Table, t), scriptLocator, EntityVocabulary.Medium));
        foreach (var t in commands.TablesWritten)
            links.Add(new LinkFact(EntityVocabulary.Writes, me, new(EntityVocabulary.Table, t), scriptLocator, EntityVocabulary.Medium));

        foreach (var trigger in job.Descendants().Where(e => XmlFacts.Is(e, "Trigger", "FileEvent", "FileTrigger", "Event")))
        {
            var kind = XmlFacts.Attr(trigger, "type", "kind");
            var isFile = XmlFacts.Is(trigger, "FileEvent", "FileTrigger") || (kind is not null && kind.Contains("file", StringComparison.OrdinalIgnoreCase));
            var path = XmlFacts.AttrOrChild(trigger, "path", "file", "filename");
            if (!isFile || path is null) continue;
            links.Add(new LinkFact(EntityVocabulary.Triggers, new(EntityVocabulary.File, path), me, At(trigger), EntityVocabulary.High,
                Attrs.Of(("event", XmlFacts.Attr(trigger, "event", "on")))));
        }
        return true;
    }

    // ── what both carry in their header ─────────────────────────────────

    private static readonly (string[] Names, string Key)[] HeaderKeys =
    {
        (new[] { "Title" }, "title"),
        (new[] { "Host", "HostDst", "Agent" }, "host"),
        (new[] { "Queue" }, "queue"),
        (new[] { "Login" }, "login"),
        (new[] { "Schedule", "Calendar", "Runs" }, "schedule"),
        (new[] { "Doc", "Description", "Documentation" }, "description"),
    };

    private static readonly string[] NotifyNames = { "Notify", "Notification", "Notifications", "OnError", "Alert", "Mail", "Contact" };

    /// <summary>
    /// Header values — as attributes of the element or as its child elements — each stated by the chunk it
    /// sits in, so the schedule is cited by the schedule element and not by the whole plan.
    /// </summary>
    private static void ReadHeader(
        XElement owner, string name, IReadOnlyList<EvidenceChunkDraft> chunks, List<EntityFact> entities, Func<XElement, bool> skip)
    {
        string? At(XElement e) => XmlFacts.ChunkFor(chunks, XmlFacts.XPathOf(e));

        // On the element itself.
        var own = new List<(string Key, object? Value)>();
        foreach (var (names, key) in HeaderKeys)
            if (XmlFacts.Attr(owner, names) is { } v) own.Add((key, AdapterText.Clip(v, 500)));
        var ownEmails = XmlFacts.Emails(XmlFacts.Attr(owner, NotifyNames));
        if (ownEmails.Count > 0) own.Add(("notify", ownEmails.ToList()));
        if (XmlFacts.Attr(owner, "Page", "Pager", "OnCall") is { } ownPage) own.Add(("pages", ownPage));
        if (own.Count > 0)
            entities.Add(new EntityFact(EntityVocabulary.Job, name, At(owner), Attrs.Of(own.Select(p => (p.Key, p.Value)).ToArray())));

        // As child elements.
        foreach (var child in owner.Elements())
        {
            if (skip(child)) continue;
            var pairs = new List<(string Key, object? Value)>();
            foreach (var (names, key) in HeaderKeys)
                if (XmlFacts.Is(child, names) && XmlFacts.Text(child) is { } text)
                    pairs.Add((key, AdapterText.Clip(text, 500)));

            if (XmlFacts.Is(child, NotifyNames))
            {
                var emails = XmlFacts.Emails(child.ToString());
                if (emails.Count > 0) pairs.Add(("notify", emails.ToList()));
                if (XmlFacts.Attr(child, "Page", "Pager", "OnCall") is { } page) pairs.Add(("pages", page));
            }
            if (pairs.Count > 0)
                entities.Add(new EntityFact(EntityVocabulary.Job, name, At(child), Attrs.Of(pairs.ToArray())));
        }
    }

    private static string? Summary(int plans, int tasks, int jobs)
    {
        var parts = new List<string>();
        if (plans > 0) parts.Add($"{plans} job plan{(plans == 1 ? "" : "s")} with {tasks} task{(tasks == 1 ? "" : "s")}");
        if (jobs > 0) parts.Add($"{jobs} job{(jobs == 1 ? "" : "s")}");
        return parts.Count == 0 ? null : "Read " + string.Join(" and ", parts) + ".";
    }
}
