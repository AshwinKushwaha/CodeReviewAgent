using System.Text.Json.Serialization;

namespace CodeReviewAgent.Models;

/// <summary>
/// Represents a single rule violation found during code review.
/// </summary>
public sealed class ReviewResult
{
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("ruleViolated")]
    public string RuleViolated { get; set; } = string.Empty;

    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = string.Empty;

    [JsonPropertyName("suggestedFix")]
    public string SuggestedFix { get; set; } = string.Empty;
}
