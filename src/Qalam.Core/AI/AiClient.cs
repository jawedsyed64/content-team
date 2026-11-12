using Qalam.Core.Models;

namespace Qalam.Core.AI;

public sealed class AiRequest
{
    public string Model { get; set; } = "";
    public string System { get; set; } = "";
    public string UserMessage { get; set; } = "";
    public int MaxTokens { get; set; } = 8000;
    public bool UseWebSearch { get; set; }
    public int MaxSearches { get; set; } = 5;
    /// <summary>For tracing/tests: which agent made the call.</summary>
    public AgentId? Agent { get; set; }
}

public sealed class AiResponse
{
    public string Text { get; set; } = "";
    public string StopReason { get; set; } = "end_turn";
    public List<SourceLink> Citations { get; set; } = new();
    public List<SourceLink> SearchResults { get; set; } = new();
    public TokenUsage Usage { get; set; } = new();

    /// <summary>False when the model hit the token limit and the output was cut off.</summary>
    public bool IsComplete => StopReason != "max_tokens";
}

public enum AiErrorKind
{
    Authentication,
    Permission,
    BadRequest,
    NotFound,
    RateLimited,
    Overloaded,
    Server,
    Network,
    Timeout,
    InvalidResponse,
    MissingApiKey
}

public sealed class AiException : Exception
{
    public AiErrorKind Kind { get; }
    public int? StatusCode { get; }

    public AiException(AiErrorKind kind, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public bool IsRetryable => Kind is AiErrorKind.RateLimited or AiErrorKind.Overloaded
        or AiErrorKind.Server or AiErrorKind.Network or AiErrorKind.Timeout;

    /// <summary>Plain-language explanation for the dashboard.</summary>
    public string UserMessage => Kind switch
    {
        AiErrorKind.MissingApiKey => "No Claude API key is set. Add it in Settings.",
        AiErrorKind.Authentication => "The Claude API key was rejected. Check it in Settings.",
        AiErrorKind.Permission => "Your API key does not have permission for this request (for example, web search may not be enabled for your organisation). " + Message,
        AiErrorKind.NotFound => "The selected model was not found. Choose another model in Settings. " + Message,
        AiErrorKind.RateLimited => "Rate limit reached. Wait a minute and resume.",
        AiErrorKind.Overloaded => "The Claude API is temporarily overloaded. Resume in a few minutes.",
        AiErrorKind.Network => "Network problem — check your internet connection and resume.",
        AiErrorKind.Timeout => "The request timed out. Resume to try again.",
        _ => Message
    };
}

public interface IAiClient
{
    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct);
}
