namespace CodeReviewAgent.Models;

/// <summary>
/// Represents a single changed line within a diff hunk.
/// </summary>
public sealed class DiffLine
{
    /// <summary>Line number in the target branch file.</summary>
    public int LineNumber { get; set; }

    /// <summary>The text content of the line.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Whether this line was added (+) or context.</summary>
    public DiffLineType Type { get; set; }
}

public enum DiffLineType
{
    Context,
    Added,
    Modified
}
