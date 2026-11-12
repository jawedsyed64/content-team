namespace Qalam.Core.Models;

/// <summary>What the user asks for. Every field except Topic is optional.</summary>
public sealed class ContentBrief
{
    public string Topic { get; set; } = "";
    public ContentFormat Format { get; set; } = ContentFormat.BlogPost;
    public int? WordCount { get; set; }
    public string? Audience { get; set; }
    public string? Tone { get; set; }
    public string? Language { get; set; }
    public string? Platform { get; set; }
    public string? PrimaryKeyword { get; set; }
    public string? Notes { get; set; }
}

public sealed class StageState
{
    public StageKind Kind { get; set; }
    public AgentId? Agent { get; set; }
    public StageStatus Status { get; set; } = StageStatus.Pending;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? OutputVersionId { get; set; }
    public int Attempts { get; set; }
    public List<string> Questions { get; set; } = new();
    /// <summary>True when the stage stopped because context files still contain placeholders.</summary>
    public bool BlockedByContext { get; set; }
}

public sealed record SourceLink(string Url, string Title, string? CitedText = null);

public sealed class TokenUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int WebSearches { get; set; }
}

/// <summary>An immutable saved output. Never overwritten — edits create a new version.</summary>
public sealed class ArtifactVersion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectId { get; set; } = "";
    public ArtifactKind Kind { get; set; }
    public int VersionNumber { get; set; }
    public string Author { get; set; } = "";
    public AgentId? Agent { get; set; }
    public string Content { get; set; } = "";
    public string? ParentId { get; set; }
    public int RevisionRound { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<SourceLink> Sources { get; set; } = new();
    public List<SourceLink> ConsultedSources { get; set; } = new();
    public TokenUsage? Usage { get; set; }
    public bool IsIncomplete { get; set; }
    public string? Note { get; set; }
    /// <summary>Structured data (JSON) for machine-readable artifacts such as quality reports.</summary>
    public string? Data { get; set; }
}

public sealed class AgentMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public MessageType Type { get; set; }
    public string Summary { get; set; } = "";
    public string? VersionId { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}

public sealed class ActivityEntry
{
    public DateTime At { get; set; } = DateTime.UtcNow;
    public ActivityLevel Level { get; set; }
    public AgentId? Agent { get; set; }
    public string Text { get; set; } = "";
}

public sealed class QuestionAnswer
{
    public StageKind Stage { get; set; }
    public List<string> Questions { get; set; } = new();
    public string Answer { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
}

public sealed class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Title { get; set; } = "";
    public ContentBrief Brief { get; set; } = new();
    public ProjectStatus Status { get; set; } = ProjectStatus.NotStarted;
    public string? StatusDetail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<StageState> Stages { get; set; } = new();
    public int RevisionRound { get; set; }
    /// <summary>QA-requested revisions since the last user feedback (limited by MaxRevisionRounds).</summary>
    public int QaRevisionsInCycle { get; set; }
    /// <summary>The user chose to proceed although some context fields are still unfilled.</summary>
    public bool AllowIncompleteContext { get; set; }
    public bool QaApproved { get; set; }
    public string? CurrentDraftVersionId { get; set; }
    public string? LatestQualityReportVersionId { get; set; }
    public List<AgentMessage> Messages { get; set; } = new();
    public List<ActivityEntry> Activity { get; set; } = new();
    public List<QuestionAnswer> Answers { get; set; } = new();
    public List<string> PendingRevisionRequests { get; set; } = new();
    public string? UserDecisionNote { get; set; }

    public StageState Stage(StageKind kind) => Stages.First(s => s.Kind == kind);

    public static List<StageState> DefaultStages() => new()
    {
        new() { Kind = StageKind.Strategy, Agent = AgentId.Strategist },
        new() { Kind = StageKind.Research, Agent = AgentId.ResearchAnalyst },
        new() { Kind = StageKind.Planning, Agent = AgentId.Ideation },
        new() { Kind = StageKind.Draft, Agent = AgentId.Writer },
        new() { Kind = StageKind.Seo, Agent = AgentId.Seo },
        new() { Kind = StageKind.Editing, Agent = AgentId.CopyEditor },
        new() { Kind = StageKind.QualityAssurance, Agent = AgentId.QualityManager },
        new() { Kind = StageKind.Delivery, Agent = null },
    };
}

public sealed class CalendarEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public DateTime Date { get; set; }
    public string Title { get; set; } = "";
    public string Format { get; set; } = "";
    public string Category { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Keyword { get; set; } = "";
    public string Notes { get; set; } = "";
    public string? ProjectId { get; set; }
}
