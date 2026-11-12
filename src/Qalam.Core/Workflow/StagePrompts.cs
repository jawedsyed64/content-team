using System.Text;
using Qalam.Core.Models;

namespace Qalam.Core.Workflow;

/// <summary>The task prompts and output contracts each stage hands to its agent.</summary>
public static class StagePrompts
{
    public static string StageTitle(StageKind kind) => kind switch
    {
        StageKind.Strategy => "Topic selection & objective",
        StageKind.Research => "Research",
        StageKind.Planning => "Content planning",
        StageKind.Draft => "Content creation",
        StageKind.Seo => "SEO optimisation",
        StageKind.Editing => "Editing & proofreading",
        StageKind.QualityAssurance => "Quality assurance",
        StageKind.Delivery => "Final delivery",
        _ => kind.ToString()
    };

    public static string FormatName(ContentFormat f) => f switch
    {
        ContentFormat.BlogPost => "Blog post",
        ContentFormat.Article => "Article",
        ContentFormat.WebsiteContent => "Website content",
        ContentFormat.ProfessionalReport => "Professional report",
        ContentFormat.Newsletter => "Newsletter",
        ContentFormat.LinkedInArticle => "LinkedIn article",
        ContentFormat.LinkedInPost => "LinkedIn post",
        ContentFormat.SocialCaptions => "Social media captions",
        ContentFormat.PressRelease => "Press release",
        ContentFormat.EducationalContent => "Educational content",
        ContentFormat.ProductDescription => "Product description",
        ContentFormat.EmailCampaign => "Email campaign",
        ContentFormat.ResearchArticle => "Research-based article",
        ContentFormat.ThoughtLeadership => "Thought leadership piece",
        _ => f.ToString()
    };

    public static string FormatGuidance(ContentFormat f) => f switch
    {
        ContentFormat.BlogPost or ContentFormat.Article =>
            "Web article: H1 headline, short engaging introduction, H2/H3 sections, scannable paragraphs, clear conclusion with a call to action.",
        ContentFormat.WebsiteContent =>
            "Website page copy: clear value proposition at the top, benefit-led sections, concise paragraphs, one primary call to action.",
        ContentFormat.ProfessionalReport =>
            "Professional report: title, executive summary, numbered sections, findings supported by sources, conclusions and recommendations.",
        ContentFormat.Newsletter =>
            "Newsletter issue: subject line and preview text first, friendly opening, 2–4 short sections, links, sign-off.",
        ContentFormat.LinkedInArticle =>
            "LinkedIn article: professional first-person or brand voice, strong opening line, short paragraphs, takeaway-driven, closing question to invite discussion.",
        ContentFormat.LinkedInPost =>
            "LinkedIn post: under 3,000 characters, hook in the first two lines, line breaks for readability, 3–5 relevant hashtags at the end.",
        ContentFormat.SocialCaptions =>
            "Set of social captions for several platforms, each labelled with its platform, respecting platform norms and length limits.",
        ContentFormat.PressRelease =>
            "Press release: headline, sub-headline, dateline, lead paragraph answering who/what/when/where/why, body, a quotation ONLY if supplied by the user, boilerplate, media contact placeholder.",
        ContentFormat.EducationalContent =>
            "Educational piece: learning objectives, plain-language explanations, worked examples, summary and check-your-understanding questions.",
        ContentFormat.ProductDescription =>
            "Product description: benefit-led headline, short paragraph, bullet list of features with benefits, specifications only as supplied, call to action. Never invent specifications.",
        ContentFormat.EmailCampaign =>
            "Email campaign: subject line options, preview text, email body with one clear call to action; if a sequence is requested, label each email.",
        ContentFormat.ResearchArticle =>
            "Research-based article: thorough, evidence-led, every claim sourced, methodology caveats noted, balanced treatment of disagreements.",
        ContentFormat.ThoughtLeadership =>
            "Thought leadership: a clear, defensible point of view, supported by evidence, with practical implications for the reader.",
        _ => ""
    };

    public static string BriefBlock(Project p)
    {
        var b = p.Brief;
        var sb = new StringBuilder();
        sb.AppendLine("<input source=\"user brief\">");
        sb.AppendLine($"Topic: {b.Topic}");
        sb.AppendLine($"Format: {FormatName(b.Format)} — {FormatGuidance(b.Format)}");
        sb.AppendLine($"Target length: {(b.WordCount is > 0 ? $"about {b.WordCount} words" : "not specified (use the content strategy)")}");
        sb.AppendLine($"Audience: {Or(b.Audience, "as described in the Audience Profile")}");
        sb.AppendLine($"Tone: {Or(b.Tone, "as described in the Brand Voice file")}");
        sb.AppendLine($"Language: {Or(b.Language, "as described in the Brand Voice file")}");
        sb.AppendLine($"Publication platform: {Or(b.Platform, "not specified")}");
        sb.AppendLine($"Primary keyword: {Or(b.PrimaryKeyword, "not specified (SEO Specialist to recommend)")}");
        if (!string.IsNullOrWhiteSpace(b.Notes)) sb.AppendLine($"Additional notes: {b.Notes}");
        sb.AppendLine("</input>");
        return sb.ToString();
    }

    public static string AnswersBlock(Project p)
    {
        if (p.Answers.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("<input source=\"answers from the user to earlier questions — treat as authoritative\">");
        foreach (var a in p.Answers)
        {
            foreach (var q in a.Questions) sb.AppendLine($"Q: {q}");
            sb.AppendLine($"A: {a.Answer}");
            sb.AppendLine();
        }
        sb.AppendLine("</input>");
        sb.AppendLine("Do not ask again about anything answered above.");
        return sb.ToString();
    }

    public static string Input(string source, string? content) =>
        string.IsNullOrWhiteSpace(content) ? "" : $"<input source=\"{source}\">\n{content.Trim()}\n</input>\n";

    public static string SourcesList(IEnumerable<SourceLink> sources)
    {
        var list = sources.ToList();
        if (list.Count == 0) return "";
        var sb = new StringBuilder();
        int n = 1;
        foreach (var s in list) sb.AppendLine($"{n++}. {s.Title} — {s.Url}");
        return sb.ToString();
    }

    private static string Or(string? v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();

    // ---------- Output contracts ----------

    public const string StrategyFormat = """
        Respond in Markdown with exactly these sections:
        ## Content objective
        ## Fit with our content strategy
        ## Audience focus
        ## Key message
        ## Recommended format and length
        ## Angles worth considering
        ## Research questions for the Research Analyst
        ## Success criteria
        """;

    public const string ResearchFormat = """
        Respond in Markdown with exactly these sections. Write the source URL next to EVERY fact, figure and quotation.
        ## Summary
        ## Key facts
        - fact — [source title](URL) (publication date if stated)
        ## Statistics
        ## Examples and case studies
        ## Current developments and trends
        ## Expert views and quotations
        (verbatim quotations only, with who said it and the source link; leave empty if none found)
        ## Disagreements and uncertainty
        ## Could not verify
        (claims you looked for but could not confirm — writers must not use these as facts)
        ## Sources
        1. Title — URL — why it is reliable
        """;

    public const string PlanningFormat = """
        Respond in Markdown with exactly these sections:
        ## Headline options
        (five numbered options)
        ## Recommended headline
        ## Angle
        ## Storytelling approach
        ## Hook options
        (two or three alternative opening paragraphs, using only facts from the research brief)
        ## Outline
        (H2/H3 structure; under each heading list the key points and which research facts/sources support them)
        ## Call to action
        """;

    public static string DraftFormat(Project p) =>
        $"""
        Return ONLY the complete piece in Markdown, starting with the H1 headline (or subject line for emails/newsletters).
        Target length: {(p.Brief.WordCount is > 0 ? $"about {p.Brief.WordCount} words (within ±10%)" : "appropriate to the format and the content strategy")}.
        Cite sources as inline Markdown links on the claims they support, using only links from the research brief.
        End with a "## Sources" section listing the links used.
        Do not add notes to the editor in the body.
        """;

    public const string SeoFormat = """
        Respond in exactly two parts, using these marker lines:

        === SEO REPORT ===
        ## Search intent
        ## Primary keyword
        ## Secondary keywords
        (your judgement from search intent and live results — no volume or difficulty figures)
        ## SEO title options
        ## Meta title
        (max 60 characters)
        ## Meta description
        (max 155 characters)
        ## URL slug
        ## Heading structure recommendations
        ## Internal link suggestions
        ## Changes made to the draft

        === OPTIMIZED DRAFT ===
        (the complete optimised draft in Markdown — keep every fact, figure, quotation and link unchanged)
        """;

    public const string EditingFormat = """
        Respond in exactly two parts, using these marker lines:

        === EDITED DRAFT ===
        (the complete edited draft in Markdown)

        === CHANGE LOG ===
        (bullet list of the kinds of changes you made, with a few representative before → after examples,
        and any passages you think the writer should look at again)
        """;

    public const string CalendarFormat = """
        Return ONLY a JSON object, no prose, with this shape:
        {
          "themes": ["monthly or weekly themes"],
          "entries": [
            {"date": "YYYY-MM-DD", "title": "working title", "format": "e.g. Blog post", "category": "content category from the strategy",
             "platform": "where it is published", "keyword": "suggested primary keyword or empty", "notes": "angle and purpose"}
          ],
          "questions": ["only if you need information from the user"]
        }
        """;

    public const string SocialFormat = """
        Respond in Markdown with these sections, each ready to copy and paste:
        ## LinkedIn post
        ## Facebook caption
        ## Instagram caption
        ## X (Twitter)
        (a single post and a 3–5 post thread; every post max 280 characters, show the character count after each)
        ## Short video / reel ideas
        (three ideas: hook in the first 3 seconds, outline, on-screen text)
        ## Platform headlines and hooks
        ## Hashtags
        ## Calls to action
        """;
}
