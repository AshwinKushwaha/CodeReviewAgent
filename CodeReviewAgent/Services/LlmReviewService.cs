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
    // Default chunk size — can be overridden via appsettings.json "MaxChunkChars"
    private const int DefaultMaxChunkChars = 10_000;
    private const int MinChunkChars = 500;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    private const int MaxSplitDepth = 3;

    private readonly int _maxChunkChars;

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
    private string? _rulesContent;

    public LlmReviewService(string githubToken, string model = "openai/gpt-5.2", string? endpoint = null, int maxChunkChars = DefaultMaxChunkChars)
    {
        _maxChunkChars = maxChunkChars > 0 ? maxChunkChars : DefaultMaxChunkChars;
        var credential = new ApiKeyCredential(githubToken);
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint ?? DefaultEndpoint) };
        _chatClient = new ChatClient(model, credential, clientOptions);
    }

    /// <summary>
    /// Reviews all file diffs against the rules document.
    /// Automatically chunks large diffs to stay within token limits.
    /// If a chunk still exceeds the limit (413), it is split further and retried.
    /// </summary>
    /// <summary>
    /// Sends a small test request to verify the endpoint and model are reachable.
    /// Prints detailed diagnostics on failure.
    /// </summary>
    public async Task<bool> TestConnectivityAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var messages = new List<ChatMessage>
            {
                ChatMessage.CreateUserMessage("Reply with exactly: OK")
            };
            var options = new ChatCompletionOptions { Temperature = 0.0f };
            ChatCompletion result = await _chatClient.CompleteChatAsync(messages, options, cts.Token);
            _requestTimestamps.Enqueue(DateTime.UtcNow);
            return result.Content.Count > 0;
        }
        catch (ClientResultException ex)
        {
            Console.Error.WriteLine($"  API returned HTTP {ex.Status}");
            Console.Error.WriteLine($"  Message: {ex.Message}");

            if (ex.Status == 401)
                Console.Error.WriteLine("  ? Your GitHubToken is invalid or expired. Generate a new one at https://github.com/settings/tokens");
            else if (ex.Status == 404)
                Console.Error.WriteLine("  ? Model or endpoint not found. Verify \"Model\" and \"GitHubModelsEndpoint\" in appsettings.json.");
            else if (ex.Status == 403)
                Console.Error.WriteLine("  ? Access denied. Ensure your GitHub account has Copilot access and the token has the required scopes.");

            return false;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("  Request timed out after 30 seconds.");
            Console.Error.WriteLine("  ? Check your network connection or try a different endpoint.");
            return false;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"  Network error: {ex.Message}");
            Console.Error.WriteLine("  ? Check your internet connection, firewall, or proxy settings.");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  Error type: {ex.GetType().Name}");
            Console.Error.WriteLine($"  Message: {ex.Message}");
            if (ex.InnerException is not null)
                Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
            return false;
        }
    }

    /// <summary>
    /// Reviews all file diffs against the rules document.
    /// Automatically chunks large diffs to stay within token limits.
    /// If a chunk still exceeds the limit (413) or times out, it is split further and retried.
    /// </summary>
    public async Task<List<ReviewResult>> ReviewAsync(List<FileDiff> diffs, string rulesContent)
    {
        _rulesContent = rulesContent;
        var allResults = new List<ReviewResult>();
        var chunks = BuildChunks(diffs);
        var totalChunks = chunks.Count;

        Console.WriteLine($"Sending {totalChunks} chunk(s) to LLM for review...");

        // Track (Label, Chunk, SplitDepth) to prevent infinite splitting
        var pendingChunks = new Queue<(string Label, List<FileDiff> Chunk, int Depth)>();
        for (int i = 0; i < chunks.Count; i++)
            pendingChunks.Enqueue(((i + 1).ToString(), chunks[i], 0));

        var totalProcessed = 0;
        var overallStopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (pendingChunks.Count > 0)
        {
            var (label, chunk, depth) = pendingChunks.Dequeue();
            totalProcessed++;

            await WaitForRateLimitAsync();

            var chunkFiles = string.Join(", ", chunk.Select(c => Path.GetFileName(c.FilePath)));
            var chunkLines = chunk.Sum(c => c.Lines.Count);
            Console.WriteLine($"  [{totalProcessed}] Chunk {label} ({chunk.Count} file(s), {chunkLines} lines): {chunkFiles}");

            var chunkStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var userMessage = BuildUserMessage(chunk);
            var (results, wasTooLarge) = await SendToLlmWithSplitAsync(userMessage);
            chunkStopwatch.Stop();

            if (wasTooLarge)
            {
                if (depth >= MaxSplitDepth)
                {
                    Console.Error.WriteLine($"      Max split depth ({MaxSplitDepth}) reached. Skipping chunk {label}.");
                    continue;
                }

                // Multi-file chunk: split into individual files
                if (chunk.Count > 1)
                {
                    var subChunks = SplitChunkFurther(chunk);
                    Console.WriteLine($"      Splitting into {subChunks.Count} sub-chunks...");
                    for (int s = 0; s < subChunks.Count; s++)
                        pendingChunks.Enqueue(($"{label}.{s + 1}", subChunks[s], depth + 1));
                }
                else
                {
                    // Single file: halve the lines
                    var lineBatches = HalveFileDiff(chunk[0]);
                    if (lineBatches.Count <= 1)
                    {
                        Console.Error.WriteLine($"      Cannot split further ({chunkLines} lines). Skipping chunk {label}.");
                        continue;
                    }
                    Console.WriteLine($"      Halving into {lineBatches.Count} batches...");
                    for (int s = 0; s < lineBatches.Count; s++)
                        pendingChunks.Enqueue(($"{label}.{s + 1}", [lineBatches[s]], depth + 1));
                }
            }
            else
            {
                Console.WriteLine($"      Done in {chunkStopwatch.Elapsed.TotalSeconds:F1}s — {results.Count} violation(s)");
                allResults.AddRange(results);
            }
        }

        overallStopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine($"LLM review completed in {overallStopwatch.Elapsed.TotalSeconds:F0}s ({totalProcessed} request(s), {allResults.Count} violation(s))");

        return allResults;
    }

    /// <summary>
    /// Groups file diffs into chunks that fit within the token budget.
    /// Large single files are pre-split into line batches.
    /// </summary>
    private List<List<FileDiff>> BuildChunks(List<FileDiff> diffs)
    {
        var chunks = new List<List<FileDiff>>();
        var currentChunk = new List<FileDiff>();
        int currentSize = 0;

        foreach (var diff in diffs)
        {
            var diffText = FormatFileDiff(diff);
            var diffSize = diffText.Length;

            // If a single file exceeds the limit, split it into line batches
            if (diffSize > _maxChunkChars)
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

            if (currentSize + diffSize > _maxChunkChars && currentChunk.Count > 0)
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
    private List<FileDiff> SplitSingleFileDiff(FileDiff diff)
    {
        var batches = new List<FileDiff>();
        var linesPerBatch = Math.Max(10, _maxChunkChars / 60);

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
    /// Splits a FileDiff in half. Used when a chunk times out — always produces 2 smaller pieces.
    /// Returns the original in a single-item list if it has fewer than 10 lines (cannot split further).
    /// </summary>
    private static List<FileDiff> HalveFileDiff(FileDiff diff)
    {
        if (diff.Lines.Count < 10)
            return [diff];

        var mid = diff.Lines.Count / 2;
        return
        [
            new FileDiff { FilePath = diff.FilePath, Lines = diff.Lines.Take(mid).ToList() },
            new FileDiff { FilePath = diff.FilePath, Lines = diff.Lines.Skip(mid).ToList() }
        ];
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

    private string BuildUserMessage(List<FileDiff> diffs)
    {
        var sb = new StringBuilder();

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
        var systemMessage = SystemPrompt + "\n\n## RULES DOCUMENT\n" + (_rulesContent ?? string.Empty);

        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(systemMessage),
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
                using var cts = new CancellationTokenSource(RequestTimeout);
                ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options, cts.Token);
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
                Console.WriteLine($"      Rate limited (429). Retry {attempt}/{RetryMaxAttempts} after {delay.TotalSeconds:F0}s...");

                if (attempt == RetryMaxAttempts)
                {
                    Console.Error.WriteLine("      Max retries exceeded. Skipping this chunk.");
                    return ([], false);
                }

                await Task.Delay(delay);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"      Request timed out after {RequestTimeout.TotalMinutes:F0} min. Will split into smaller chunks.");
                return ([], true);
            }
            catch (Exception ex) when (ex.InnerException is OperationCanceledException)
            {
                Console.WriteLine($"      Request timed out after {RequestTimeout.TotalMinutes:F0} min. Will split into smaller chunks.");
                return ([], true);
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
