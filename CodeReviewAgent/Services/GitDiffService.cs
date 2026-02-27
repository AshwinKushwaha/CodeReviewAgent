using CodeReviewAgent.Models;
using LibGit2Sharp;

namespace CodeReviewAgent.Services;

/// <summary>
/// Extracts diff information between two branches using LibGit2Sharp.
/// Only retrieves changed/added lines with minimal surrounding context.
/// </summary>
public sealed class GitDiffService
{
    private const int ContextLines = 3;

    /// <summary>
    /// Gets file diffs between <paramref name="sourceBranch"/> and <paramref name="targetBranch"/>.
    /// Returns only files with additions/modifications, each with line numbers mapped to the target branch.
    /// </summary>
    public Task<List<FileDiff>> GetDiffsAsync(string repoPath, string sourceBranch, string targetBranch)
    {
        var diffs = new List<FileDiff>();

        using var repo = new Repository(repoPath);

        var source = ResolveBranch(repo, sourceBranch);
        var target = ResolveBranch(repo, targetBranch);

        Console.WriteLine($"Comparing {sourceBranch} ({source.Sha[..8]}) ? {targetBranch} ({target.Sha[..8]})");

        // Diff from target to source to see what source introduces
        var changes = repo.Diff.Compare<Patch>(target.Tree, source.Tree);

        foreach (var entry in changes)
        {
            // Skip deletions and binary files
            if (entry.Status is ChangeKind.Deleted || entry.IsBinaryComparison)
                continue;

            var fileDiff = ParsePatchEntry(entry);
            if (fileDiff.Lines.Count > 0)
            {
                diffs.Add(fileDiff);
            }
        }

        Console.WriteLine($"Found {diffs.Count} changed file(s) with relevant diffs.");
        return Task.FromResult(diffs);
    }

    private static Commit ResolveBranch(Repository repo, string branchName)
    {
        // Try exact branch name first, then with remote prefix
        var branch = repo.Branches[branchName]
                     ?? repo.Branches[$"origin/{branchName}"]
                     ?? throw new InvalidOperationException(
                         $"Branch '{branchName}' not found. Available branches: " +
                         string.Join(", ", repo.Branches.Select(b => b.FriendlyName)));

        return branch.Tip;
    }

    /// <summary>
    /// Parses a LibGit2Sharp PatchEntryChanges into our FileDiff model.
    /// Extracts added lines with their target-branch line numbers and surrounding context.
    /// </summary>
    private static FileDiff ParsePatchEntry(PatchEntryChanges entry)
    {
        var fileDiff = new FileDiff { FilePath = entry.Path };
        var patchText = entry.Patch;

        if (string.IsNullOrWhiteSpace(patchText))
            return fileDiff;

        // Parse the unified diff format manually to extract line numbers accurately
        var lines = patchText.Split('\n');
        int newLineNumber = 0;
        int pendingDeletions = 0;
        var allParsedLines = new List<(int NewLineNum, string Content, bool IsAdded, bool IsModified, bool IsContext)>();

        foreach (var rawLine in lines)
        {
            // Hunk header: @@ -oldStart,oldCount +newStart,newCount @@
            if (rawLine.StartsWith("@@"))
            {
                var hunkInfo = ParseHunkHeader(rawLine);
                if (hunkInfo.HasValue)
                {
                    newLineNumber = hunkInfo.Value.NewStart;
                }
                pendingDeletions = 0;
                continue;
            }

            if (rawLine.StartsWith("---") || rawLine.StartsWith("+++"))
                continue;

            if (rawLine.StartsWith('+'))
            {
                bool isModified = pendingDeletions > 0;
                if (isModified)
                    pendingDeletions--;

                allParsedLines.Add((newLineNumber, rawLine[1..], IsAdded: !isModified, IsModified: isModified, IsContext: false));
                newLineNumber++;
            }
            else if (rawLine.StartsWith('-'))
            {
                // Track deletions to detect modifications (a '+' following '-' lines)
                pendingDeletions++;
                continue;
            }
            else
            {
                // Context line (unchanged) — reset pending deletions
                pendingDeletions = 0;
                allParsedLines.Add((newLineNumber, rawLine.Length > 0 ? rawLine[1..] : rawLine, IsAdded: false, IsModified: false, IsContext: true));
                newLineNumber++;
            }
        }

        // Now filter: keep added/modified lines and their surrounding context
        var changedIndices = new HashSet<int>();
        for (int i = 0; i < allParsedLines.Count; i++)
        {
            if (allParsedLines[i].IsAdded || allParsedLines[i].IsModified)
                changedIndices.Add(i);
        }

        var includeIndices = new HashSet<int>();
        foreach (var idx in changedIndices)
        {
            for (int c = Math.Max(0, idx - ContextLines); c <= Math.Min(allParsedLines.Count - 1, idx + ContextLines); c++)
            {
                includeIndices.Add(c);
            }
        }

        foreach (var idx in includeIndices.Order())
        {
            var (lineNum, content, isAdded, isModified, _) = allParsedLines[idx];
            fileDiff.Lines.Add(new DiffLine
            {
                LineNumber = lineNum,
                Content = content,
                Type = isModified ? DiffLineType.Modified : isAdded ? DiffLineType.Added : DiffLineType.Context
            });
        }

        return fileDiff;
    }

    private static (int OldStart, int NewStart)? ParseHunkHeader(string header)
    {
        // Format: @@ -oldStart[,oldCount] +newStart[,newCount] @@
        var match = System.Text.RegularExpressions.Regex.Match(
            header, @"@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@");

        if (!match.Success)
            return null;

        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
    }
}
