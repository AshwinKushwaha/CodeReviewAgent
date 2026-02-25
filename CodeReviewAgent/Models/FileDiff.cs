namespace CodeReviewAgent.Models;

/// <summary>
/// Represents all changed hunks for a single file.
/// </summary>
public sealed class FileDiff
{
    /// <summary>Relative path of the file within the repository.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>The diff lines including context and added/modified lines.</summary>
    public List<DiffLine> Lines { get; set; } = [];
}
