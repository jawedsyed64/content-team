using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Qalam.Core.Agents;
using Qalam.Core.AI;
using Qalam.Core.Documents;
using Qalam.Core.Models;
using Qalam.Core.Quality;
using Qalam.Core.Storage;
using Xunit;

namespace Qalam.Core.Tests;

public class QualityRulesTests
{
    [Fact]
    public void Parses_fenced_json_and_approves_clean_report()
    {
        var r = QualityRules.Parse(Scripts.QaJson("approve"), "clean draft")!;
        Assert.Equal(QaDecision.Approve, r.Decision);
        Assert.Equal(7, r.Scores.Count);
        Assert.Equal(8.0, r.Overall);
    }

    [Fact]
    public void Needs_source_marker_blocks_approval()
    {
        var r = QualityRules.Parse(Scripts.QaJson("approve"), "Sales rose [NEEDS SOURCE: figure].")!;
        Assert.Equal(QaDecision.Revise, r.Decision);
        Assert.True(r.GateOverrodeModel);
    }

    [Fact]
    public void Resolved_factual_issue_does_not_block()
    {
        var r = QualityRules.Parse(Scripts.QaJson("approve", Scripts.FactualIssue(resolved: true)), "draft")!;
        Assert.Equal(QaDecision.Approve, r.Decision);
    }

    [Fact]
    public void Missing_criteria_block_approval_and_unusable_json_returns_null()
    {
        var partial = "{\"decision\":\"approve\",\"scores\":[{\"criterion\":\"relevance\",\"score\":9}]}";
        Assert.Equal(QaDecision.Revise, QualityRules.Parse(partial, "d")!.Decision);
        Assert.Null(QualityRules.Parse("I think it is fine.", "d"));
    }

    [Fact]
    public void Markdown_report_always_carries_the_disclaimer_and_low_score_advice()
    {
        var r = QualityRules.Parse(Scripts.QaJson("approve"), "d")!;
        r.Scores[0].Score = 4;
        r.Scores[0].Recommendation = "Add sources for paragraph 3.";
        var md = QualityRules.ToMarkdown(r);
        Assert.Contains(QualityRules.Disclaimer, md);
        Assert.Contains("Improve: Add sources for paragraph 3.", md);
    }
}

public class AgentOutputTests
{
    [Theory]
    [InlineData("Body\n\nQUESTIONS FOR USER\n- One?\n- Two?", 2)]
    [InlineData("Body\n\n## Questions for the user\n1. One?", 1)]
    [InlineData("Body\n\nQUESTIONS FOR USER:\n- None", 0)]
    [InlineData("Body with no questions", 0)]
    public void Splits_questions(string text, int expected)
    {
        var (body, questions) = AgentOutput.SplitQuestions(text);
        Assert.StartsWith("Body", body);
        Assert.DoesNotContain("QUESTIONS", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, questions.Count);
    }

    [Fact]
    public void Splits_marker_sections()
    {
        var parts = AgentOutput.SplitSections("=== SEO REPORT ===\nreport\n=== OPTIMIZED DRAFT ===\n# Draft\ntext");
        Assert.Equal("report", parts["SEO REPORT"]);
        Assert.Equal("# Draft\ntext", parts["OPTIMIZED DRAFT"]);
    }

    [Fact]
    public void Every_agent_has_a_distinct_role_and_prompt()
    {
        Assert.Equal(8, AgentCatalog.All.Count);
        Assert.Equal(8, AgentCatalog.All.Select(a => a.Instructions).Distinct().Count());
        Assert.Contains("NEVER invent", AgentCatalog.ResearchAnalyst.Instructions);
        Assert.Contains("must not approve", AgentCatalog.QualityManager.Instructions);
    }
}

public class ClaudeClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<string> Bodies { get; } = new();
        public List<HttpRequestMessage> Requests { get; } = new();
        public StubHandler(params HttpResponseMessage[] responses) => _responses = new(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return _responses.Dequeue();
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string WebAnswer = """
        {"content":[
          {"type":"server_tool_use","id":"s1","name":"web_search","input":{"query":"x"}},
          {"type":"web_search_tool_result","tool_use_id":"s1","content":[{"type":"web_search_result","url":"https://a.example/1","title":"A"}]},
          {"type":"text","text":"Half of firms use X.","citations":[{"type":"web_search_result_location","url":"https://a.example/1","title":"A","cited_text":"50% use X"}]},
          {"type":"text","text":" More text."}
        ],"stop_reason":"end_turn","usage":{"input_tokens":100,"output_tokens":20,"server_tool_use":{"web_search_requests":1}}}
        """;

    [Fact]
    public async Task Sends_headers_web_search_tool_and_collects_citations()
    {
        var stub = new StubHandler(Json(HttpStatusCode.OK, WebAnswer));
        var client = new ClaudeClient(new HttpClient(stub), () => "sk-ant-test");

        var r = await client.CompleteAsync(new AiRequest { Model = "claude-sonnet-5-5", System = "sys", UserMessage = "hi", UseWebSearch = true }, default);

        Assert.Equal("Half of firms use X. More text.", r.Text);
        Assert.Single(r.Citations);
        Assert.Equal("50% use X", r.Citations[0].CitedText);
        Assert.Single(r.SearchResults);
        Assert.Equal(1, r.Usage.WebSearches);
        Assert.Equal("sk-ant-test", stub.Requests[0].Headers.GetValues("x-api-key").Single());
        Assert.Equal(ClaudeClient.ApiVersion, stub.Requests[0].Headers.GetValues("anthropic-version").Single());
        var body = JsonNode.Parse(stub.Bodies[0])!;
        Assert.Equal("web_search", body["tools"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("sys", body["system"]!.GetValue<string>());
    }

    [Fact]
    public async Task Continues_pause_turn_and_retries_overloaded()
    {
        var paused = """{"content":[{"type":"text","text":"Part one."}],"stop_reason":"pause_turn","usage":{"input_tokens":1,"output_tokens":1}}""";
        var done = """{"content":[{"type":"text","text":" Part two."}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""";
        var stub = new StubHandler(
            Json(HttpStatusCode.OK, paused),
            Json((HttpStatusCode)529, """{"error":{"type":"overloaded_error","message":"Overloaded"}}"""),
            Json(HttpStatusCode.OK, done));
        var delays = new List<TimeSpan>();
        var client = new ClaudeClient(new HttpClient(stub), () => "k", (t, _) => { delays.Add(t); return Task.CompletedTask; });

        var r = await client.CompleteAsync(new AiRequest { Model = "m", UserMessage = "go" }, default);

        Assert.Equal("Part one. Part two.", r.Text);
        Assert.Single(delays);
        var third = JsonNode.Parse(stub.Bodies[2])!["messages"]!.AsArray();
        Assert.Equal(2, third.Count);
        Assert.Equal("assistant", third[1]!["role"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(401, AiErrorKind.Authentication, false)]
    [InlineData(404, AiErrorKind.NotFound, false)]
    [InlineData(429, AiErrorKind.RateLimited, true)]
    [InlineData(529, AiErrorKind.Overloaded, true)]
    [InlineData(500, AiErrorKind.Server, true)]
    public void Maps_http_errors(int code, AiErrorKind kind, bool retryable)
    {
        var ex = ClaudeClient.MapError((HttpStatusCode)code, """{"error":{"message":"boom"}}""");
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(retryable, ex.IsRetryable);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Missing_key_and_bad_key_fail_without_retry()
    {
        await Assert.ThrowsAsync<AiException>(() =>
            new ClaudeClient(new HttpClient(new StubHandler()), () => null).CompleteAsync(new AiRequest(), default));

        var stub = new StubHandler(Json(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid x-api-key"}}"""));
        var ex = await Assert.ThrowsAsync<AiException>(() =>
            new ClaudeClient(new HttpClient(stub), () => "bad").CompleteAsync(new AiRequest { Model = "m" }, default));
        Assert.Equal(AiErrorKind.Authentication, ex.Kind);
        Assert.Single(stub.Requests);
    }
}

public class StorageTests
{
    [Fact]
    public void Versions_are_write_once_and_numbered_per_kind()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qalam-st-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repo = new JsonProjectRepository(dir);
            var a = repo.AddVersion(new ArtifactVersion { ProjectId = "p1", Kind = ArtifactKind.Draft, Content = "one" });
            var b = repo.AddVersion(new ArtifactVersion { ProjectId = "p1", Kind = ArtifactKind.Draft, Content = "two" });
            Assert.Equal(1, a.VersionNumber);
            Assert.Equal(2, b.VersionNumber);
            Assert.Throws<InvalidOperationException>(() => repo.AddVersion(a));
            Assert.Equal("one", repo.GetVersion("p1", a.Id)!.Content);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Context_edits_keep_history_and_brand_docx_text_is_extracted()
    {
        using var h = new Harness(fillContext: false);
        h.Context.Write(Context.ContextFileKind.BrandVoice, "new text");
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(h.Folder, "context", "history")));

        var docx = Path.Combine(h.Folder, "brand.docx");
        using (var zip = ZipFile.Open(docx, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("word/document.xml");
            using var w = new StreamWriter(e.Open());
            w.Write("""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>We are warm</w:t></w:r></w:p><w:p><w:r><w:t>and practical.</w:t></w:r></w:p></w:body></w:document>""");
        }
        h.Docs.Import(docx);
        var text = h.Docs.CombinedText(5000);
        Assert.Contains("We are warm\nand practical.", text);

        // Brand documents reach brand-aware agents only.
        var writerPrompt = h.Runner.BuildSystemPrompt(AgentCatalog.Writer, "", false);
        var editorPrompt = h.Runner.BuildSystemPrompt(AgentCatalog.CopyEditor, "", false);
        Assert.Contains("We are warm", writerPrompt);
        Assert.DoesNotContain("We are warm", editorPrompt);
    }
}
