namespace Qalam.Core.Models;

public enum AgentId
{
    Strategist,
    ResearchAnalyst,
    Ideation,
    Writer,
    CopyEditor,
    Seo,
    SocialMedia,
    QualityManager
}

public enum StageKind
{
    Strategy,
    Research,
    Planning,
    Draft,
    Seo,
    Editing,
    QualityAssurance,
    Delivery
}

public enum StageStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Incomplete,
    NeedsInput,
    Skipped
}

public enum ProjectStatus
{
    NotStarted,
    Running,
    Paused,
    NeedsInput,
    Failed,
    AwaitingApproval,
    Approved,
    Rejected
}

public enum ArtifactKind
{
    Objective,
    ResearchBrief,
    ContentPlan,
    Draft,
    SeoReport,
    EditLog,
    QualityReport,
    SocialPack,
    Calendar,
    SeoToolReport
}

public enum MessageType
{
    Brief,
    Objective,
    ResearchBrief,
    ContentPlan,
    Draft,
    Recommendations,
    EditedDraft,
    QualityReport,
    RevisionRequest,
    Approval,
    Question,
    UserAnswer,
    UserFeedback,
    UserDecision
}

public enum ActivityLevel
{
    Info,
    Warning,
    Error
}

public enum ContentFormat
{
    BlogPost,
    Article,
    WebsiteContent,
    ProfessionalReport,
    Newsletter,
    LinkedInArticle,
    LinkedInPost,
    SocialCaptions,
    PressRelease,
    EducationalContent,
    ProductDescription,
    EmailCampaign,
    ResearchArticle,
    ThoughtLeadership
}
