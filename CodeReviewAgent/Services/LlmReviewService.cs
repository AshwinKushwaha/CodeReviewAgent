using System.Text;
using System.Text.Json;
using CodeReviewAgent.Models;
using OpenAI.Chat;

namespace CodeReviewAgent.Services;

/// <summary>
/// Sends diff data and rules to OpenAI for code review.
/// Handles chunking of large diffs and aggregation of results.
/// </summary>
public sealed class LlmReviewService
{
    // Conservative token budget – leaves room for system prompt, rules, and response
    private const int MaxChunkChars = 12_000;

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
        """;

    private readonly ChatClient _chatClient;

    public LlmReviewService(string apiKey, string model = "gpt-4o")
    {
        _chatClient = new ChatClient(model, apiKey);
    }

    /// <summary>
    /// Reviews all file diffs against the rules document.
    /// Automatically chunks large diffs to stay within token limits.
    /// </summary>
    public async Task<List<ReviewResult>> ReviewAsync(List<FileDiff> diffs, string rulesContent)
    {
        var allResults = new List<ReviewResult>();
        var chunks = BuildChunks(diffs);

        Console.WriteLine($"Sending {chunks.Count} chunk(s) to LLM for review...");

        for (int i = 0; i < chunks.Count; i++)
        {
            Console.WriteLine($"  Processing chunk {i + 1}/{chunks.Count}...");

            var userMessage = BuildUserMessage(chunks[i], rulesContent);
            var results = await SendToLlmAsync(userMessage);
            allResults.AddRange(results);
        }

        return allResults;
    }

    /// <summary>
    /// Groups file diffs into chunks that fit within the token budget.
    /// Each chunk contains one or more complete file diffs.
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

            // If a single file exceeds the limit, it gets its own chunk
            if (diffSize > MaxChunkChars)
            {
                if (currentChunk.Count > 0)
                {
                    chunks.Add(currentChunk);
                    currentChunk = [];
                    currentSize = 0;
                }
                chunks.Add([diff]);
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

    private async Task<List<ReviewResult>> SendToLlmAsync(string userMessage)
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

        ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options);

        var responseText = completion.Content[0].Text.Trim();

        // Strip markdown code fences if the model wraps the response
        responseText = StripMarkdownFences(responseText);

        try
        {
            var results = JsonSerializer.Deserialize<List<ReviewResult>>(responseText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return results ?? [];
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Warning: Failed to parse LLM response as JSON. {ex.Message}");
            Console.Error.WriteLine($"Raw response: {responseText[..Math.Min(500, responseText.Length)]}");
            return [];
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
