using System.Text;
using System.Text.RegularExpressions;
using CodeReviewAgent.Models;

namespace CodeReviewAgent.Services;

/// <summary>
/// Pre-filters rules based on file types and changed content in each chunk,
/// so only relevant rules are sent to the LLM — reducing token usage per request.
/// </summary>
public sealed partial class RuleRelevanceFilter
{
    private static readonly HashSet<string> CssExtensions = [".css", ".scss", ".less", ".cshtml", ".razor", ".html"];
    private static readonly HashSet<string> CSharpExtensions = [".cs"];
    private static readonly HashSet<string> UiExtensions = [".cs", ".cshtml", ".razor", ".js", ".ts", ".html", ".jsx", ".tsx"];
    private static readonly HashSet<string> DbExtensions = [".sql"];

    private readonly List<(string Id, string FullText)> _rules;

    public RuleRelevanceFilter(string rulesContent)
    {
        _rules = ParseRules(rulesContent);
    }

    public int TotalRules => _rules.Count;

    /// <summary>
    /// Returns only the rules relevant to the files and changed content in the given chunk.
    /// </summary>
    public string GetFilteredRules(List<FileDiff> chunk, out int includedCount)
    {
        var extensions = chunk
            .Select(f => Path.GetExtension(f.FilePath).ToLowerInvariant())
            .ToHashSet();

        var changedContent = string.Concat(
            chunk.SelectMany(f => f.Lines)
                 .Where(l => l.Type is DiffLineType.Added or DiffLineType.Modified)
                 .Select(l => l.Content));

        var filtered = _rules
            .Where(r => IsRuleRelevant(r.Id, extensions, changedContent))
            .ToList();

        includedCount = filtered.Count;

        if (filtered.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("# Applicable Code Review Rules");
        sb.AppendLine();
        foreach (var (_, text) in filtered)
        {
            sb.AppendLine(text);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static List<(string Id, string FullText)> ParseRules(string content)
    {
        var rules = new List<(string Id, string FullText)>();
        var lines = content.Split('\n');
        string? currentId = null;
        var currentBlock = new StringBuilder();

        foreach (var line in lines)
        {
            var match = RuleHeaderRegex().Match(line);
            if (match.Success)
            {
                if (currentId is not null)
                    rules.Add((currentId, currentBlock.ToString().TrimEnd()));

                currentId = match.Groups[1].Value;
                currentBlock.Clear();
                currentBlock.AppendLine(line);
            }
            else if (currentId is not null)
            {
                if (line.TrimStart().StartsWith("## ") || line.Trim() == "---")
                {
                    rules.Add((currentId, currentBlock.ToString().TrimEnd()));
                    currentId = null;
                    currentBlock.Clear();
                }
                else
                {
                    currentBlock.AppendLine(line);
                }
            }
        }

        if (currentId is not null)
            rules.Add((currentId, currentBlock.ToString().TrimEnd()));

        return rules;
    }

    private static bool IsRuleRelevant(string ruleId, HashSet<string> extensions, string changedContent)
    {
        // CSS rules: only for stylesheets and markup files
        if (ruleId.StartsWith("RULE-CSS"))
            return extensions.Overlaps(CssExtensions);

        // String concatenation: only when '+' or String.Concat appears in C# code
        if (ruleId is "RULE-STR-001")
            return extensions.Overlaps(CSharpExtensions) &&
                   (changedContent.Contains(" + ") || changedContent.Contains("String.Concat", StringComparison.OrdinalIgnoreCase));

        // Empty string comparison: only when "" appears
        if (ruleId is "RULE-STR-002")
            return extensions.Overlaps(CSharpExtensions) && changedContent.Contains("\"\"");

        // String equality: only when == or != operators appear
        if (ruleId is "RULE-STR-003")
            return extensions.Overlaps(CSharpExtensions) &&
                   (changedContent.Contains("==") || changedContent.Contains("!="));

        // Catch-all for any future RULE-STR rules
        if (ruleId.StartsWith("RULE-STR"))
            return extensions.Overlaps(CSharpExtensions);

        // Magic numbers: only when multi-digit numeric literals are present
        if (ruleId is "RULE-CS-004")
            return extensions.Overlaps(CSharpExtensions) && MultiDigitNumberRegex().IsMatch(changedContent);

        // General C# code style rules
        if (ruleId.StartsWith("RULE-CS"))
            return extensions.Overlaps(CSharpExtensions);

        // Architecture rules
        if (ruleId.StartsWith("RULE-ARCH"))
            return extensions.Overlaps(CSharpExtensions);

        // UX rules: UI-related files
        if (ruleId.StartsWith("RULE-UX"))
            return extensions.Overlaps(UiExtensions);

        // Database rules: SQL files or content with DB-related patterns
        if (ruleId.StartsWith("RULE-DB"))
            return extensions.Overlaps(DbExtensions) ||
                   changedContent.Contains("ALTER TABLE", StringComparison.OrdinalIgnoreCase) ||
                   changedContent.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase);

        // Test rules
        if (ruleId.StartsWith("RULE-TEST"))
            return extensions.Overlaps(CSharpExtensions);

        // Include unknown rules by default to avoid missing violations
        return true;
    }

    [GeneratedRegex(@"^###\s+(RULE-\w+-\d+)")]
    private static partial Regex RuleHeaderRegex();

    [GeneratedRegex(@"(?<![.\w])\d{2,}(?!\w)")]
    private static partial Regex MultiDigitNumberRegex();
}
