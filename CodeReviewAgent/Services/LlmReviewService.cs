using System.ClientModel;
using System.Text;
using System.Text.Json;
using CodeReviewAgent.Models;
using OpenAI;
using OpenAI.Chat;

namespace CodeReviewAgent.Services;

/// <summary>
/// Sends diff data and rules to GitHub Copilot (via GitHub Models) for code review.
/// Handles chunking of large diffs and aggregation of results.
/// </summary>
public sealed class LlmReviewService
{
    // GitHub Models free tier: 8K token limit per request.
    // ~6K chars reserved for system prompt + rules ? ~2K for diff per chunk.
    private const int MaxChunkChars = 4_000;
    private const int MinChunkChars = 500;

    private const string DefaultEndpoint = "https://models.github.ai/inference";

    private const string SystemPrompt =
		"""
        You are a strict senior code reviewer. Only evaluate provided diff lines against the rules.
        You MUST return ONLY a valid JSON array of violation objects with no additional text.
        Each object must have these exact fields:
        - "file": string (file path)
        - "line": integer (line number in the target branch)
        - "ruleViolated": string (the rule that was violated)
        - "explanation": string (why it violates the rule)
        - "suggestedFix": string (how to fix it)

        If no violations are found, return an empty JSON array: []

        IMPORTANT:
        - Review ONLY added/modified lines (marked with [ADDED]).
        - Context lines (marked with [CTX]) are for reference only — do NOT flag them.
        - Line numbers shown are from the target branch.
        - Be precise with line numbers — use the exact numbers provided.

        STRICT FALSE-POSITIVE RULES — you MUST follow these:
        - Only report a violation if the code CLEARLY and DEFINITIVELY breaks a rule.
        - Do NOT report a violation if the rule does not apply to the data type or context.
          For example: string comparison rules do NOT apply to integer, bool, enum, or object comparisons.
        - Do NOT report speculative or "might be" violations. If you are unsure, do NOT include it.
        - Do NOT report a violation and then say "this is not actually a violation" in the explanation.
        - Do NOT flag code that is already compliant with the rule.
        - Do NOT flag the same logical issue on the same line under multiple rules.
        - Every violation you return MUST have a non-empty suggestedFix.
        - If after analysis you find zero real violations, return an empty array [].
        """;

    // GitHub Models free tier: 10 requests per 60 seconds. Stay under the limit.
    private const int MaxRequestsPerMinute = 9;
    private const int RetryMaxAttempts = 5;
    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(60);

    private readonly ChatClient _chatClient;
    private readonly Queue<DateTime> _requestTimestamps = new();

    public LlmReviewService(string githubToken, string model = "openai/gpt-5.2", string? endpoint = null)
    {
        var credential = new ApiKeyCredential(githubToken);
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint ?? DefaultEndpoint) };
        _chatClient = new ChatClient(model, credential, clientOptions);
    }

    /// <summary>
    /// Reviews all file diffs against the rules document.
    /// Automatically chunks large diffs to stay within token limits.
    /// If a chunk still exceeds the limit (413), it is split further and retried.
    /// </summary>
    public async Task<List<ReviewResult>> ReviewAsync(List<FileDiff> diffs, string rulesContent)
    {
        var allResults = new List<ReviewResult>();
        var chunks = BuildChunks(diffs);

        Console.WriteLine($"Sending {chunks.Count} chunk(s) to LLM for review...");

        var pendingChunks = new Queue<(string Label, List<FileDiff> Chunk)>();
        for (int i = 0; i < chunks.Count; i++)
            pendingChunks.Enqueue(((i + 1).ToString(), chunks[i]));

        var totalProcessed = 0;

        while (pendingChunks.Count > 0)
        {
            var (label, chunk) = pendingChunks.Dequeue();
            totalProcessed++;

            await WaitForRateLimitAsync();
            Console.WriteLine($"  Processing chunk {label} (attempt {totalProcessed})...");

            var userMessage = BuildUserMessage(chunk, rulesContent);
            var (results, wasTooLarge) = await SendToLlmWithSplitAsync(userMessage);

            if (wasTooLarge)
            {
                // Split this chunk into smaller pieces and re-queue
                var subChunks = SplitChunkFurther(chunk);
                if (subChunks.Count > 1)
                {
                    Console.WriteLine($"  Chunk {label} too large. Splitting into {subChunks.Count} sub-chunks...");
                    for (int s = 0; s < subChunks.Count; s++)
                        pendingChunks.Enqueue(($"{label}.{s + 1}", subChunks[s]));
                }
                else
                {
                    // Single file with too many lines — split the file's lines into batches
                    var lineBatches = SplitSingleFileDiff(chunk[0]);
                    Console.WriteLine($"  Large single file. Splitting into {lineBatches.Count} line batches...");
                    for (int s = 0; s < lineBatches.Count; s++)
                        pendingChunks.Enqueue(($"{label}.{s + 1}", [lineBatches[s]]));
                }
            }
            else
            {
                allResults.AddRange(results);
            }
        }

        return allResults;
    }

    /// <summary>
    /// Groups file diffs into chunks that fit within the token budget.
    /// Large single files are pre-split into line batches.
    /// </summary>
    private static List<List<FileDiff>> BuildChunks(List<FileDiff> diffs)
    {
        var chunks = new List<List<FileDiff>>();
        var currentChunk = new List<FileDiff>();
        int currentSize = 0;

        foreach (var diff in diffs)
        {
            var diffText = FormatFileDiff(diff);
            var diffSize = diffText.Length;

            // If a single file exceeds the limit, split it into line batches
            if (diffSize > MaxChunkChars)
            {
                if (currentChunk.Count > 0)
                {
                    chunks.Add(currentChunk);
                    currentChunk = [];
                    currentSize = 0;
                }

                var batches = SplitSingleFileDiff(diff);
                foreach (var batch in batches)
                    chunks.Add([batch]);

                continue;
            }

            if (currentSize + diffSize > MaxChunkChars && currentChunk.Count > 0)
            {
                chunks.Add(currentChunk);
                currentChunk = [];
                currentSize = 0;
            }

            currentChunk.Add(diff);
            currentSize += diffSize;
        }

        if (currentChunk.Count > 0)
            chunks.Add(currentChunk);

        return chunks;
    }

    /// <summary>
    /// Splits a single large FileDiff into multiple smaller FileDiffs by partitioning its lines.
    /// </summary>
    private static List<FileDiff> SplitSingleFileDiff(FileDiff diff)
    {
        var batches = new List<FileDiff>();
        // Estimate ~60 chars per line on average
        var linesPerBatch = Math.Max(10, MaxChunkChars / 60);

        for (int i = 0; i < diff.Lines.Count; i += linesPerBatch)
        {
            var batchLines = diff.Lines.Skip(i).Take(linesPerBatch).ToList();
            batches.Add(new FileDiff
            {
                FilePath = diff.FilePath,
                Lines = batchLines
            });
        }

        return batches;
    }

    /// <summary>
    /// Splits a multi-file chunk into individual single-file chunks.
    /// </summary>
    private static List<List<FileDiff>> SplitChunkFurther(List<FileDiff> chunk)
    {
        if (chunk.Count <= 1)
            return [chunk];

        return chunk.Select(d => new List<FileDiff> { d }).ToList();
    }

    private static string BuildUserMessage(List<FileDiff> diffs, string rulesContent)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## RULES DOCUMENT");
        sb.AppendLine(rulesContent);
        sb.AppendLine();
        sb.AppendLine("## CODE CHANGES TO REVIEW");
        sb.AppendLine("Review ONLY lines marked [ADDED]. Lines marked [CTX] are context only.");
        sb.AppendLine();

        foreach (var diff in diffs)
        {
            sb.Append(FormatFileDiff(diff));
        }

        sb.AppendLine();
        sb.AppendLine("Return ONLY a JSON array of violations. No markdown fences, no explanation outside JSON.");

        return sb.ToString();
    }

    private static string FormatFileDiff(FileDiff diff)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### File: {diff.FilePath}");

        foreach (var line in diff.Lines)
        {
            var marker = line.Type == DiffLineType.Added ? "[ADDED]" : "[CTX]";
            sb.AppendLine($"  {marker} L{line.LineNumber}: {line.Content}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Sends a request to the LLM with automatic retry on HTTP 429 (rate limit).
    /// Returns (results, wasTooLarge): if wasTooLarge is true, the caller should split and retry.
    /// </summary>
    private async Task<(List<ReviewResult> Results, bool WasTooLarge)> SendToLlmWithSplitAsync(string userMessage)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(SystemPrompt),
            ChatMessage.CreateUserMessage(userMessage)
        };

        var options = new ChatCompletionOptions
        {
            Temperature = 0.0f,
            TopP = 0.1f
        };

        var baseDelay = TimeSpan.FromSeconds(15);

        for (int attempt = 1; attempt <= RetryMaxAttempts; attempt++)
        {
            try
            {
                ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options);
                _requestTimestamps.Enqueue(DateTime.UtcNow);

                var responseText = completion.Content[0].Text.Trim();
                responseText = StripMarkdownFences(responseText);

                try
                {
                    var results = JsonSerializer.Deserialize<List<ReviewResult>>(responseText, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    return (results ?? [], false);
                }
                catch (JsonException ex)
                {
                    Console.Error.WriteLine($"Warning: Failed to parse LLM response as JSON. {ex.Message}");
                    Console.Error.WriteLine($"Raw response: {responseText[..Math.Min(500, responseText.Length)]}");
                    return ([], false);
                }
            }
            catch (ClientResultException ex) when (ex.Status == 413)
            {
                Console.WriteLine($"  Payload too large (413). Will split and retry.");
                return ([], true);
            }
            catch (ClientResultException ex) when (ex.Status == 429)
            {
                var delay = baseDelay * Math.Pow(2, attempt - 1);
                Console.WriteLine($"  Rate limited (429). Retry {attempt}/{RetryMaxAttempts} after {delay.TotalSeconds:F0}s...");

                if (attempt == RetryMaxAttempts)
                {
                    Console.Error.WriteLine("  Max retries exceeded. Skipping this chunk.");
                    return ([], false);
                }

                await Task.Delay(delay);
            }
        }

        return ([], false);
    }

    /// <summary>
    /// Proactively waits if we are approaching the rate limit to avoid 429 errors.
    /// Tracks request timestamps within a sliding 60-second window.
    /// </summary>
    private async Task WaitForRateLimitAsync()
    {
        // Evict timestamps outside the sliding window
        while (_requestTimestamps.Count > 0 && DateTime.UtcNow - _requestTimestamps.Peek() > RateLimitWindow)
        {
            _requestTimestamps.Dequeue();
        }

        if (_requestTimestamps.Count >= MaxRequestsPerMinute)
        {
            var oldestTimestamp = _requestTimestamps.Peek();
            var waitUntil = oldestTimestamp + RateLimitWindow;
            var delay = waitUntil - DateTime.UtcNow;

            if (delay > TimeSpan.Zero)
            {
                Console.WriteLine($"  Rate limit pacing: waiting {delay.TotalSeconds:F0}s before next request...");
                await Task.Delay(delay);
            }

            // Evict again after waiting
            while (_requestTimestamps.Count > 0 && DateTime.UtcNow - _requestTimestamps.Peek() > RateLimitWindow)
            {
                _requestTimestamps.Dequeue();
            }
        }
    }

    private static string StripMarkdownFences(string text)
    {
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
                text = text[(firstNewline + 1)..];
        }

        if (text.EndsWith("```"))
        {
            text = text[..^3].TrimEnd();
        }

        return text;
    }
}
