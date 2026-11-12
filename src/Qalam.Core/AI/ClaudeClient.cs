using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qalam.Core.Models;

namespace Qalam.Core.AI;

/// <summary>
/// Minimal client for the Anthropic Messages API (https://docs.claude.com).
/// Supports the server-side web search tool, automatic continuation of
/// pause_turn responses, citation capture and retries with back-off.
/// </summary>
public sealed class ClaudeClient : IAiClient
{
    public const string Endpoint = "https://api.anthropic.com/v1/messages";
    public const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _maxAttempts;

    public ClaudeClient(HttpClient http, Func<string?> apiKeyProvider,
        Func<TimeSpan, CancellationToken, Task>? delay = null, int maxAttempts = 3)
    {
        _http = http;
        _apiKey = apiKeyProvider;
        _delay = delay ?? Task.Delay;
        _maxAttempts = Math.Max(1, maxAttempts);
        if (_http.Timeout == TimeSpan.FromSeconds(100)) _http.Timeout = TimeSpan.FromMinutes(10);
    }

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken ct)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new AiException(AiErrorKind.MissingApiKey, "API key missing.");

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = request.UserMessage }
        };

        var result = new AiResponse();
        var text = new StringBuilder();

        // pause_turn: the server paused a long tool-using turn; send the partial assistant turn back to continue.
        for (int turn = 0; turn < 6; turn++)
        {
            var body = BuildBody(request, messages);
            var json = await SendWithRetryAsync(body, key!, ct).ConfigureAwait(false);

            var content = json["content"] as JsonArray ?? new JsonArray();
            ParseContent(content, text, result);
            AddUsage(json, result.Usage);

            result.StopReason = json["stop_reason"]?.GetValue<string>() ?? "end_turn";
            if (result.StopReason != "pause_turn") break;

            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });
        }

        result.Text = text.ToString().Trim();
        result.Citations = result.Citations
            .GroupBy(c => c.Url).Select(g => g.First()).ToList();
        result.SearchResults = result.SearchResults
            .GroupBy(c => c.Url).Select(g => g.First()).ToList();
        if (result.Text.Length == 0 && result.StopReason != "max_tokens")
            throw new AiException(AiErrorKind.InvalidResponse, "The model returned an empty response.");
        return result;
    }

    internal static string BuildBody(AiRequest request, JsonArray messages)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens,
            ["system"] = request.System,
            ["messages"] = messages.DeepClone(),
        };
        if (request.UseWebSearch)
        {
            body["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "web_search_20250305",
                    ["name"] = "web_search",
                    ["max_uses"] = request.MaxSearches,
                }
            };
        }
        return body.ToJsonString();
    }

    internal static void ParseContent(JsonArray content, StringBuilder text, AiResponse result)
    {
        foreach (var block in content.OfType<JsonObject>())
        {
            var type = block["type"]?.GetValue<string>();
            if (type == "text")
            {
                text.Append(block["text"]?.GetValue<string>() ?? "");
                if (block["citations"] is JsonArray cits)
                {
                    foreach (var c in cits.OfType<JsonObject>())
                    {
                        var url = c["url"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(url)) continue;
                        result.Citations.Add(new SourceLink(url,
                            c["title"]?.GetValue<string>() ?? url,
                            c["cited_text"]?.GetValue<string>()));
                    }
                }
            }
            else if (type == "web_search_tool_result" && block["content"] is JsonArray results)
            {
                foreach (var r in results.OfType<JsonObject>())
                {
                    var url = r["url"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(url)) continue;
                    result.SearchResults.Add(new SourceLink(url, r["title"]?.GetValue<string>() ?? url));
                }
            }
        }
    }

    private static void AddUsage(JsonNode json, TokenUsage usage)
    {
        var u = json["usage"];
        if (u == null) return;
        usage.InputTokens += u["input_tokens"]?.GetValue<int>() ?? 0;
        usage.OutputTokens += u["output_tokens"]?.GetValue<int>() ?? 0;
        usage.WebSearches += u["server_tool_use"]?["web_search_requests"]?.GetValue<int>() ?? 0;
    }

    private async Task<JsonNode> SendWithRetryAsync(string body, string key, CancellationToken ct)
    {
        AiException? last = null;
        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            TimeSpan? retryAfter = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("x-api-key", key);
                req.Headers.Add("anthropic-version", ApiVersion);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                var payload = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    try { return JsonNode.Parse(payload) ?? throw new JsonException("null"); }
                    catch (JsonException ex)
                    {
                        throw new AiException(AiErrorKind.InvalidResponse, "Could not read the API response.", (int)resp.StatusCode, ex);
                    }
                }

                retryAfter = resp.Headers.RetryAfter?.Delta;
                last = MapError(resp.StatusCode, payload);
            }
            catch (AiException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (TaskCanceledException ex)
            {
                last = new AiException(AiErrorKind.Timeout, "Request timed out.", null, ex);
            }
            catch (HttpRequestException ex)
            {
                last = new AiException(AiErrorKind.Network, ex.Message, null, ex);
            }

            if (!last!.IsRetryable || attempt == _maxAttempts) throw last;
            var wait = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(3, attempt)); // 3s, 9s, ...
            await _delay(wait, ct).ConfigureAwait(false);
        }
        throw last ?? new AiException(AiErrorKind.Network, "Request failed.");
    }

    internal static AiException MapError(HttpStatusCode status, string payload)
    {
        string message = payload;
        try
        {
            var node = JsonNode.Parse(payload);
            message = node?["error"]?["message"]?.GetValue<string>() ?? payload;
        }
        catch (JsonException) { }

        var code = (int)status;
        var kind = code switch
        {
            400 => AiErrorKind.BadRequest,
            401 => AiErrorKind.Authentication,
            403 => AiErrorKind.Permission,
            404 => AiErrorKind.NotFound,
            408 => AiErrorKind.Timeout,
            413 => AiErrorKind.BadRequest,
            429 => AiErrorKind.RateLimited,
            529 => AiErrorKind.Overloaded,
            >= 500 => AiErrorKind.Server,
            _ => AiErrorKind.BadRequest
        };
        return new AiException(kind, message, code);
    }
}
