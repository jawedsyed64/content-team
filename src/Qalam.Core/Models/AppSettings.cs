namespace Qalam.Core.Models;

/// <summary>Non-secret settings. The API key is stored separately and encrypted by the app.</summary>
public sealed class AppSettings
{
    public static readonly string[] KnownModels =
    {
        "claude-sonnet-5-5",
        "claude-opus-5-5",
        "claude-haiku-5-5",
    };

    public string Model { get; set; } = "claude-sonnet-5-5";

    /// <summary>Optional stronger model for research and quality review. Empty = use Model.</summary>
    public string? ReviewModel { get; set; }

    public int MaxTokensPerStage { get; set; } = 8000;
    public int MaxRevisionRounds { get; set; } = 2;
    public bool EnableWebSearch { get; set; } = true;
    public int MaxSearchesPerStage { get; set; } = 6;

    /// <summary>When true, agents stop and ask while their context files still contain [FILL IN] placeholders.</summary>
    public bool StrictContext { get; set; } = true;

    public int MaxBrandDocChars { get; set; } = 20000;

    public string ModelFor(AgentId agent) =>
        (agent is AgentId.ResearchAnalyst or AgentId.QualityManager) && !string.IsNullOrWhiteSpace(ReviewModel)
            ? ReviewModel!
            : Model;
}
