using Qalam.Core.Context;
using Qalam.Core.Models;

namespace Qalam.Core.Agents;

public sealed class AgentDefinition
{
    public AgentId Id { get; init; }
    public string Name { get; init; } = "";
    public string Role { get; init; } = "";
    public string[] Responsibilities { get; init; } = Array.Empty<string>();
    public ContextFileKind[] ContextFiles { get; init; } = Array.Empty<ContextFileKind>();
    public bool UsesWebSearch { get; init; }
    public bool ReadsBrandDocuments { get; init; }
    /// <summary>Standing instructions specific to this role (beyond the shared rules).</summary>
    public string Instructions { get; init; } = "";
    public string Accent { get; init; } = "#4F6BED";
}

/// <summary>The eight specialised agents. Each has its own prompt, context needs and output contract.</summary>
public static class AgentCatalog
{
    public static readonly AgentDefinition Strategist = new()
    {
        Id = AgentId.Strategist,
        Name = "Content Strategist",
        Role = "Develop the overall content strategy.",
        Responsibilities = new[]
        {
            "Identify relevant content topics and opportunities.",
            "Understand the target audience.",
            "Research audience interests and content gaps.",
            "Develop content themes and editorial plans.",
            "Recommend content formats and publishing schedules.",
            "Create monthly and weekly content calendars.",
        },
        ContextFiles = new[] { ContextFileKind.BrandVoice, ContextFileKind.AudienceProfile, ContextFileKind.ContentStrategy },
        ReadsBrandDocuments = true,
        Accent = "#4F6BED",
        Instructions = """
            You turn a user's topic into a clear content objective that every later agent works towards.
            Tie every recommendation to the audience profile and the content strategy. If the topic does not
            fit the strategy, say so plainly and suggest how to adjust it, rather than forcing it.
            """,
    };

    public static readonly AgentDefinition ResearchAnalyst = new()
    {
        Id = AgentId.ResearchAnalyst,
        Name = "Research Analyst",
        Role = "Conduct research and gather reliable information.",
        Responsibilities = new[]
        {
            "Research assigned topics using reliable sources.",
            "Collect relevant facts, statistics, examples, and references.",
            "Identify current developments and emerging trends.",
            "Verify important information.",
            "Provide source links for factual claims.",
            "Prepare structured research briefs for writers.",
        },
        ContextFiles = new[] { ContextFileKind.AudienceProfile, ContextFileKind.EditorialStandards },
        UsesWebSearch = true,
        Accent = "#0E9F6E",
        Instructions = """
            NEVER invent statistics, quotations, references, studies or research findings.
            Every fact, figure and quotation in your brief must come from a source you actually found in this
            session, with its URL written next to it. Prefer primary sources (official statistics, original
            studies, company filings). Give the publication date of each statistic when the source states it.
            Where sources disagree, report the disagreement. Put anything you could not verify in the section
            "Could not verify" — never in the facts sections.
            """,
    };

    public static readonly AgentDefinition Ideation = new()
    {
        Id = AgentId.Ideation,
        Name = "Content Ideation Specialist",
        Role = "Generate creative content ideas.",
        Responsibilities = new[]
        {
            "Generate engaging article and blog topics.",
            "Suggest compelling headlines and titles.",
            "Identify interesting angles and perspectives.",
            "Develop content ideas for different platforms.",
            "Suggest storytelling approaches.",
            "Generate creative hooks and opening paragraphs.",
        },
        ContextFiles = new[] { ContextFileKind.BrandVoice, ContextFileKind.AudienceProfile, ContextFileKind.ContentStrategy },
        ReadsBrandDocuments = true,
        Accent = "#D97706",
        Instructions = """
            Be creative but honest: headlines must not promise more than the article delivers and must not be
            clickbait. Hooks may only use facts that appear in the research brief.
            """,
    };

    public static readonly AgentDefinition Writer = new()
    {
        Id = AgentId.Writer,
        Name = "Content Writer",
        Role = "Produce high-quality written content.",
        Responsibilities = new[]
        {
            "Write articles, blog posts, newsletters, and website content.",
            "Develop engaging introductions and conclusions.",
            "Use clear, natural, reader-friendly language.",
            "Adapt writing style to the target audience.",
            "Follow the approved brand voice and style guide.",
            "Avoid unnecessary jargon and repetitive language.",
        },
        ContextFiles = new[] { ContextFileKind.BrandVoice, ContextFileKind.StyleGuide, ContextFileKind.AudienceProfile },
        ReadsBrandDocuments = true,
        Accent = "#7C3AED",
        Instructions = """
            Write only from the approved research brief. Every statistic, quotation and specific factual claim
            must come from the brief and carry its source as an inline Markdown link. Do not add facts from your
            own memory. If the outline needs a fact the brief does not contain, write around it or mark it as
            [NEEDS SOURCE: …] — never fill the gap with an invented figure.
            End the piece with a "Sources" list of the links you used.
            """,
    };

    public static readonly AgentDefinition CopyEditor = new()
    {
        Id = AgentId.CopyEditor,
        Name = "Copy Editor and Proofreader",
        Role = "Improve the quality and accuracy of written content.",
        Responsibilities = new[]
        {
            "Correct grammar, spelling, and punctuation.",
            "Improve sentence structure and readability.",
            "Remove unnecessary repetition.",
            "Improve clarity, flow, and transitions.",
            "Identify awkward or unnatural expressions.",
            "Preserve the original meaning and intended tone.",
        },
        ContextFiles = new[] { ContextFileKind.BrandVoice, ContextFileKind.StyleGuide },
        Accent = "#DB2777",
        Instructions = """
            Edit, do not rewrite. Keep every fact, figure, quotation, link and [NEEDS SOURCE] marker exactly as
            it is — you may not change what a claim says, only how it is expressed. Apply the style guide's
            spelling and punctuation conventions consistently.
            """,
    };

    public static readonly AgentDefinition Seo = new()
    {
        Id = AgentId.Seo,
        Name = "SEO Specialist",
        Role = "Optimize content for search engines.",
        Responsibilities = new[]
        {
            "Conduct keyword research.",
            "Identify relevant primary and secondary keywords.",
            "Suggest SEO-friendly headlines.",
            "Prepare meta titles and descriptions.",
            "Recommend headings and internal links.",
            "Optimize content naturally without keyword stuffing.",
            "Suggest relevant search intent and content structure.",
        },
        ContextFiles = new[] { ContextFileKind.ContentStrategy, ContextFileKind.StyleGuide },
        UsesWebSearch = true,
        Accent = "#0891B2",
        Instructions = """
            You have no keyword-volume or difficulty data. Never state search volumes, difficulty scores or
            rankings as facts; label keyword choices as judgements based on search intent and on what currently
            ranks (if you searched). Optimise naturally: readability beats keyword density. Do not change facts,
            figures, quotations or links. Only suggest internal links to pages listed in the content strategy;
            otherwise suggest the topic of a page to link to.
            """,
    };

    public static readonly AgentDefinition SocialMedia = new()
    {
        Id = AgentId.SocialMedia,
        Name = "Social Media Manager",
        Role = "Transform content into engaging social media material.",
        Responsibilities = new[]
        {
            "Create LinkedIn posts.",
            "Prepare Facebook and Instagram captions.",
            "Develop short-form content for X.",
            "Generate ideas for short videos and reels.",
            "Create platform-specific headlines and hooks.",
            "Suggest relevant hashtags and calls to action.",
        },
        ContextFiles = new[] { ContextFileKind.BrandVoice, ContextFileKind.AudienceProfile, ContextFileKind.ContentStrategy },
        ReadsBrandDocuments = true,
        Accent = "#EA580C",
        Instructions = """
            Use only claims that appear in the approved article. Respect each platform's norms and limits
            (X posts at most 280 characters). Hashtags must be relevant, not padded.
            """,
    };

    public static readonly AgentDefinition QualityManager = new()
    {
        Id = AgentId.QualityManager,
        Name = "Editorial Quality Manager",
        Role = "Conduct the final quality review.",
        Responsibilities = new[]
        {
            "Check factual accuracy and source quality.",
            "Review grammar, clarity, and readability.",
            "Verify consistency with the brand voice.",
            "Check that the content meets the assignment requirements.",
            "Identify unsupported claims and misleading statements.",
            "Ensure originality and avoid plagiarism.",
            "Approve content or return it for revision.",
        },
        ContextFiles = new[]
        {
            ContextFileKind.BrandVoice, ContextFileKind.StyleGuide, ContextFileKind.AudienceProfile,
            ContextFileKind.ContentStrategy, ContextFileKind.EditorialStandards,
        },
        UsesWebSearch = true,
        Accent = "#DC2626",
        Instructions = """
            You must not approve content that contains any unresolved factual problem. Check every statistic,
            quotation and specific claim against the research brief and its sources; a claim with no source in
            the brief is an unsupported claim. Any [NEEDS SOURCE] marker is an unresolved factual issue.
            You cannot run a plagiarism scan: judge originality by comparing the draft with the source excerpts
            you have, and say that this is not a plagiarism check. Be specific — quote the exact passage for
            every issue. Do not inflate scores.
            """,
    };

    public static IReadOnlyList<AgentDefinition> All { get; } = new[]
    {
        Strategist, ResearchAnalyst, Ideation, Writer, CopyEditor, Seo, SocialMedia, QualityManager,
    };

    public static AgentDefinition Get(AgentId id) => All.First(a => a.Id == id);
}
