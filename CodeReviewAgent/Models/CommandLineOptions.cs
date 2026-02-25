namespace CodeReviewAgent.Models;

/// <summary>
/// Holds parsed command-line arguments.
/// </summary>
public sealed class CommandLineOptions
{
    public required string RepoPath { get; init; }
    public required string SourceBranch { get; init; }
    public required string TargetBranch { get; init; }
    public required string RulesPath { get; init; }
}
