using System.Text.Json;
using Qalam.Core.Agents;
using Qalam.Core.AI;
using Qalam.Core.Context;
using Qalam.Core.Documents;
using Qalam.Core.Models;
using Qalam.Core.Storage;
using Qalam.Core.Workflow;

namespace Qalam.Core.Tests;

/// <summary>A scripted AI client. Each agent's reply is produced by a handler so tests can vary behaviour per call.</summary>
public sealed class FakeAiClient : IAiClient
{
    public List<AiRequest> Requests { get; } = new();
    public Func<AiRequest, int, CancellationToken, Task<AiResponse>> Handler { get; set; }

    public FakeAiClient() => Handler = (r, n, ct) => Task.FromResult(Scripts.Default(r, n));

    public int CallsTo(AgentId agent) => Requests.Count(r => r.Agent == agent);

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        var n = Requests.Count(r => r.Agent == request.Agent);
        return await Handler(request, n, ct);
    }
}

public static class Scripts
{
    public const string Url1 = "https://www.example.gov/report-2026";
    public const string Url2 = "https://research.example.org/study";

    public static AiResponse Text(string text, string stop = "end_turn") => new() { Text = text, StopReason = stop };

    public static AiResponse Default(AiRequest r, int callNumber) => r.Agent switch
    {
        AgentId.Strategist => Text("## Content objective\nHelp small-business owners understand X.\n## Key message\nX saves time."),
        AgentId.ResearchAnalyst => new AiResponse
        {
            Text = $"## Key facts\n- 42% of firms use X — [Gov report]({Url1}) (2026)\n## Sources\n1. Gov report — {Url1}",
            Citations = { new SourceLink(Url1, "Gov report", "42% of firms use X") },
            SearchResults = { new SourceLink(Url2, "Study") },
        },
        AgentId.Ideation => Text("## Recommended headline\nWhy X matters\n## Outline\n- Intro\n- Facts"),
        AgentId.Writer => Text(Draft(callNumber)),
        AgentId.Seo => Text("=== SEO REPORT ===\n## Meta title\nWhy X matters\n=== OPTIMIZED DRAFT ===\n" + Draft(callNumber) + "\nSEO tuned."),
        AgentId.CopyEditor => Text("=== EDITED DRAFT ===\n" + Draft(callNumber) + "\nEdited.\n=== CHANGE LOG ===\n- tightened sentences"),
        AgentId.QualityManager => Text(QaJson("approve")),
        AgentId.SocialMedia => Text("## LinkedIn post\nX matters."),
        _ => Text("ok")
    };

    public static string Draft(int n) =>
        $"# Why X matters (v{n})\n\nAccording to a [government report]({Url1}), 42% of firms use X. " +
        string.Join(" ", Enumerable.Repeat("This sentence adds useful detail for readers.", 20)) + "\n\n## Sources\n- " + Url1;

    public static string QaJson(string decision, params object[] issues)
    {
        var scores = Quality.QualityRules.Criteria.Select(c => new { criterion = c, score = 8, explanation = "Good.", recommendation = "" });
        return "```json\n" + JsonSerializer.Serialize(new
        {
            decision,
            summary = "Review summary.",
            scores,
            issues,
            revision_requests = issues.Length > 0 ? new[] { "Fix the unsupported claim in paragraph 2." } : Array.Empty<string>(),
        }) + "\n```";
    }

    public static object FactualIssue(bool resolved = false) => new
    {
        type = "unsupported_claim", severity = "major", location = "most firms love X",
        description = "No source for this claim.", fix = "Remove or source it.", resolved,
    };
}

public sealed class Harness : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "qalam-tests-" + Guid.NewGuid().ToString("N"));
    public AppSettings Settings { get; } = new() { MaxRevisionRounds = 2 };
    public FakeAiClient Ai { get; } = new();
    public JsonProjectRepository Repo { get; }
    public ContextStore Context { get; }
    public BrandDocumentStore Docs { get; }
    public AgentRunner Runner { get; }
    public WorkflowOrchestrator Workflow { get; }

    public Harness(bool fillContext = true)
    {
        Repo = new JsonProjectRepository(Folder);
        Context = new ContextStore(Folder);
        Docs = new BrandDocumentStore(Folder);
        Runner = new AgentRunner(Ai, Context, Docs, () => Settings);
        Workflow = new WorkflowOrchestrator(Repo, Runner, Context, () => Settings);
        if (fillContext) FillContext();
    }

    public void FillContext()
    {
        foreach (var kind in ContextFiles.All)
            Context.Write(kind, $"# {ContextFiles.DisplayName(kind)}\nCONTEXT-{kind}: filled in by the test.");
    }

    public Project NewProject(string topic = "Why X matters") =>
        Workflow.CreateProject(new ContentBrief { Topic = topic, Format = ContentFormat.BlogPost, WordCount = 800, Language = "English" });

    public Project Reload(Project p) => Repo.GetProject(p.Id)!;

    public void Dispose()
    {
        try { Directory.Delete(Folder, true); } catch { /* best effort */ }
    }
}
