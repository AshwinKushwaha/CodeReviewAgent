using System.Text;
using CodeReviewAgent.Models;

namespace CodeReviewAgent.Services;

/// <summary>
/// Writes review violations to a human-readable .txt report file
/// in a dedicated output folder.
/// </summary>
public sealed class ReportWriter
{
    private const string ReportFolder = "ReviewReports";

    /// <summary>
    /// Writes the review results to a timestamped .txt file inside the <c>ReviewReports</c> folder.
    /// Returns the full path of the generated report file.
    /// </summary>
    public async Task<string> WriteReportAsync(
        List<ReviewResult> results,
        string sourceBranch,
        string targetBranch)
    {
        var reportDir = Path.Combine(Directory.GetCurrentDirectory(), ReportFolder);
        Directory.CreateDirectory(reportDir);

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var fileName = $"review_{timestamp}.txt";
        var filePath = Path.Combine(reportDir, fileName);

        var sb = new StringBuilder();

        sb.AppendLine("----------------------------------------------------------------");
        sb.AppendLine("  CODE REVIEW REPORT");
        sb.AppendLine("----------------------------------------------------------------");
        sb.AppendLine();
        sb.AppendLine($"  Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  Source    : {sourceBranch}");
        sb.AppendLine($"  Target    : {targetBranch}");
        sb.AppendLine($"  Violations: {results.Count}");
        sb.AppendLine();
        sb.AppendLine("----------------------------------------------------------------");

        if (results.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("  No violations found. All changes comply with the rules.");
        }
        else
        {
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];

                sb.AppendLine();
                sb.AppendLine($"  [{i + 1}] {r.RuleViolated}");
                sb.AppendLine($"      File          : {r.File}");
                sb.AppendLine($"      Line          : {r.Line}");
                sb.AppendLine($"      Explanation   : {r.Explanation}");
                sb.AppendLine($"      Suggested Fix : {r.SuggestedFix}");
                sb.AppendLine("  ────────────────────────────────────────────────────────");
            }
        }

        sb.AppendLine();
        sb.AppendLine("----------------------------------------------------------------");
        sb.AppendLine("  END OF REPORT");
        sb.AppendLine("----------------------------------------------------------------");

        await File.WriteAllTextAsync(filePath, sb.ToString());

        return filePath;
    }
}
