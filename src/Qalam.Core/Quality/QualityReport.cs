using System.Text.Json.Nodes;
using Qalam.Core.Agents;

namespace Qalam.Core.Quality;

public enum QaDecision { Approve, Revise }

public sealed class CriterionScore
{
    public string Criterion { get; set; } = "";
    public int Score { get; set; }
    public string Explanation { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public bool IsLow => Score < QualityRules.LowScoreThreshold;
    public string DisplayName => QualityRules.DisplayName(Criterion);
}

public sealed class QualityIssue
{
    public string Type { get; set; } = "other";
    public string Severity { get; set; } = "minor";
    public string Location { get; set; } = "";
    public string Description { get; set; } = "";
    public string Fix { get; set; } = "";
    public bool Resolved { get; set; }

    public bool IsFactual => QualityRules.FactualIssueTypes.Contains(Type, StringComparer.OrdinalIgnoreCase);
}

public sealed class QualityReport
{
    public QaDecision Decision { get; set; } = QaDecision.Revise;
    /// <summary>The decision the model gave, before the code-enforced gate was applied.</summary>
    public QaDecision ModelDecision { get; set; } = QaDecision.Revise;
    public List<CriterionScore> Scores { get; set; } = new();
    public List<QualityIssue> Issues { get; set; } = new();
    public List<string> RevisionRequests { get; set; } = new();
    public List<string> GateReasons { get; set; } = new();
    public string Summary { get; set; } = "";

    public double Overall => Scores.Count == 0 ? 0 : Math.Round(Scores.Average(s => s.Score), 1);
    public bool GateOverrodeModel => ModelDecision == QaDecision.Approve && Decision == QaDecision.Revise;
    public IEnumerable<QualityIssue> UnresolvedFactualIssues => Issues.Where(i => i.IsFactual && !i.Resolved);
}

public static class QualityRules
{
    public const int LowScoreThreshold = 7;

    public const string Disclaimer =
        "An automated quality score is not proof of factual accuracy or originality. Check sources yourself before publishing.";

    public static readonly string[] Criteria =
    {
        "accuracy_and_reliability",
        "relevance",
        "readability_and_clarity",
        "grammar_and_language",
        "brand_voice_consistency",
        "seo_optimization",
        "originality_and_usefulness",
    };

    public static readonly string[] FactualIssueTypes = { "factual", "unsupported_claim", "misleading" };

    public static string DisplayName(string criterion) => criterion switch
    {
        "accuracy_and_reliability" => "Accuracy and reliability",
        "relevance" => "Relevance to the topic",
        "readability_and_clarity" => "Readability and clarity",
        "grammar_and_language" => "Grammar and language quality",
        "brand_voice_consistency" => "Brand voice consistency",
        "seo_optimization" => "SEO optimisation",
        "originality_and_usefulness" => "Originality and usefulness",
        _ => criterion
    };

    public const string OutputFormat = """
        Return ONLY a JSON object, no prose before or after, with exactly this shape:
        {
          "decision": "approve" | "revise",
          "summary": "two or three sentences",
          "scores": [
            {"criterion": "accuracy_and_reliability", "score": 1-10, "explanation": "specific reason", "recommendation": "specific fix (required if score < 7)"},
            ... one entry for each of: accuracy_and_reliability, relevance, readability_and_clarity, grammar_and_language,
                brand_voice_consistency, seo_optimization, originality_and_usefulness
          ],
          "issues": [
            {"type": "factual" | "unsupported_claim" | "misleading" | "grammar" | "brand_voice" | "requirements" | "originality" | "seo" | "other",
             "severity": "critical" | "major" | "minor",
             "location": "exact quote from the draft",
             "description": "what is wrong",
             "fix": "what the writer must do",
             "resolved": false}
          ],
          "revision_requests": ["numbered, specific instructions for the writer"],
          "questions": ["only if you need information from the user"]
        }
        Rules: "approve" is forbidden while any factual, unsupported_claim or misleading issue is unresolved,
        or while the draft contains [NEEDS SOURCE] markers. Mark an issue "resolved": true only if it was raised
        in an earlier round and the current draft fixes it.
        """;

    /// <summary>Parses the QA agent's JSON and applies the approval gate. Returns null if the JSON is unusable.</summary>
    public static QualityReport? Parse(string text, string draft)
    {
        var json = AgentOutput.TryParseJsonObject(text);
        if (json == null || json["scores"] is not JsonArray) return null;

        var report = new QualityReport
        {
            Summary = Str(json["summary"]),
            ModelDecision = Str(json["decision"]).Equals("approve", StringComparison.OrdinalIgnoreCase)
                ? QaDecision.Approve : QaDecision.Revise,
        };

        foreach (var s in ((JsonArray)json["scores"]!).OfType<JsonObject>())
        {
            report.Scores.Add(new CriterionScore
            {
                Criterion = Str(s["criterion"]),
                Score = Math.Clamp(Int(s["score"]), 0, 10),
                Explanation = Str(s["explanation"]),
                Recommendation = Str(s["recommendation"]),
            });
        }
        if (json["issues"] is JsonArray issues)
        {
            foreach (var i in issues.OfType<JsonObject>())
            {
                report.Issues.Add(new QualityIssue
                {
                    Type = Str(i["type"]).ToLowerInvariant(),
                    Severity = Str(i["severity"]).ToLowerInvariant(),
                    Location = Str(i["location"]),
                    Description = Str(i["description"]),
                    Fix = Str(i["fix"]),
                    Resolved = i["resolved"] is JsonValue v && v.TryGetValue<bool>(out var b) && b,
                });
            }
        }
        if (json["revision_requests"] is JsonArray reqs)
            report.RevisionRequests.AddRange(reqs.Select(r => Str(r)).Where(r => r.Length > 0));

        ApplyGate(report, draft);
        return report;
    }

    /// <summary>Code-enforced rule: content with unresolved factual problems is never approved.</summary>
    public static void ApplyGate(QualityReport report, string draft)
    {
        report.GateReasons.Clear();
        var factual = report.UnresolvedFactualIssues.Count();
        if (factual > 0)
            report.GateReasons.Add($"{factual} unresolved factual / unsupported-claim issue(s).");
        if (report.Issues.Any(i => !i.Resolved && i.Severity == "critical"))
            report.GateReasons.Add("Unresolved critical issue(s).");
        if (draft.Contains("[NEEDS SOURCE", StringComparison.OrdinalIgnoreCase))
            report.GateReasons.Add("Draft still contains [NEEDS SOURCE] markers.");
        var missing = Criteria.Where(c => !report.Scores.Any(s => s.Criterion == c)).ToList();
        if (missing.Count > 0)
            report.GateReasons.Add("Quality report is missing scores for: " + string.Join(", ", missing.Select(DisplayName)) + ".");

        report.Decision = report.ModelDecision == QaDecision.Approve && report.GateReasons.Count == 0
            ? QaDecision.Approve
            : QaDecision.Revise;

        if (report.Decision == QaDecision.Revise && report.RevisionRequests.Count == 0)
        {
            report.RevisionRequests.AddRange(report.Issues.Where(i => !i.Resolved)
                .Select(i => $"{i.Description} → {i.Fix}".Trim(' ', '→')));
        }
    }

    /// <summary>Human-readable report shown in Editorial Review and sent to the Writer.</summary>
    public static string ToMarkdown(QualityReport r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Quality report — {(r.Decision == QaDecision.Approve ? "APPROVED" : "RETURNED FOR REVISION")}");
        sb.AppendLine();
        sb.AppendLine($"**Overall score:** {r.Overall:0.0} / 10");
        sb.AppendLine();
        sb.AppendLine($"> {Disclaimer}");
        sb.AppendLine();
        if (r.GateOverrodeModel)
        {
            sb.AppendLine("**Approval blocked by editorial rules:** the reviewer suggested approval, but:");
            foreach (var g in r.GateReasons) sb.AppendLine($"- {g}");
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(r.Summary)) { sb.AppendLine(r.Summary); sb.AppendLine(); }
        sb.AppendLine("## Scores");
        foreach (var s in r.Scores)
        {
            sb.AppendLine($"- **{DisplayName(s.Criterion)}: {s.Score}/10** — {s.Explanation}");
            if (s.IsLow && !string.IsNullOrWhiteSpace(s.Recommendation))
                sb.AppendLine($"  - Improve: {s.Recommendation}");
        }
        if (r.Issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Issues");
            foreach (var i in r.Issues)
                sb.AppendLine($"- [{i.Severity}/{i.Type}{(i.Resolved ? ", resolved" : "")}] \"{i.Location}\" — {i.Description} Fix: {i.Fix}");
        }
        if (r.Decision == QaDecision.Revise && r.RevisionRequests.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Revision requests");
            for (int n = 0; n < r.RevisionRequests.Count; n++) sb.AppendLine($"{n + 1}. {r.RevisionRequests[n]}");
        }
        return sb.ToString();
    }

    private static string Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString() ?? "";

    private static int Int(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        }
        return 0;
    }
}
