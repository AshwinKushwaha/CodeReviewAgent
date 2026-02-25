using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using CodeReviewAgent.Models;
using CodeReviewAgent.Services;

// ── Command-line argument definitions ──────────────────────────────────────────
var repoPathOption = new Option<string>("--repoPath", "Path to the local Git repository") { IsRequired = true };
var sourceBranchOption = new Option<string>("--sourceBranch", "Branch to merge from (contains new changes)") { IsRequired = true };
var targetBranchOption = new Option<string>("--targetBranch", "Branch to merge into (base branch)") { IsRequired = true };
var rulesPathOption = new Option<string>("--rulesPath", "Path to the rules document (.txt or .md)") { IsRequired = true };

var rootCommand = new RootCommand("CodeReviewAgent – AI-powered code review on git diffs")
{
    repoPathOption,
    sourceBranchOption,
    targetBranchOption,
    rulesPathOption
};

rootCommand.SetHandler(async (context) =>
{
    var options = new CommandLineOptions
    {
        RepoPath = context.ParseResult.GetValueForOption(repoPathOption)!,
        SourceBranch = context.ParseResult.GetValueForOption(sourceBranchOption)!,
        TargetBranch = context.ParseResult.GetValueForOption(targetBranchOption)!,
        RulesPath = context.ParseResult.GetValueForOption(rulesPathOption)!
    };

    context.ExitCode = await RunReviewAsync(options);
});

return await rootCommand.InvokeAsync(args);

// ── Main orchestration ─────────────────────────────────────────────────────────
static async Task<int> RunReviewAsync(CommandLineOptions options)
{
    try
    {
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine("  CodeReviewAgent – AI Code Review");
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine();

        // 0. Load GitHub token from appsettings.json
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        var gitHubToken = configuration["GitHubToken"]
            ?? throw new InvalidOperationException(
                "GitHubToken is not configured. Set it in appsettings.json.");

        if (string.Equals(gitHubToken, "YOUR_GITHUB_PERSONAL_ACCESS_TOKEN_HERE", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "GitHubToken in appsettings.json is still the placeholder value. Replace it with your actual token.");

        var endpoint = configuration["GitHubModelsEndpoint"] ?? "https://models.github.ai/inference";
        var model = configuration["Model"] ?? "openai/gpt-5.2";

        Console.WriteLine($"Using model: {model}");
        Console.WriteLine($"Endpoint:    {endpoint}");
        Console.WriteLine();

        // 1. Load rules document
        var rulesLoader = new RulesLoader();
        var rulesContent = await rulesLoader.LoadRulesAsync(options.RulesPath);

        // 2. Extract git diffs between branches
        var gitDiffService = new GitDiffService();
        var diffs = await gitDiffService.GetDiffsAsync(
            options.RepoPath, options.SourceBranch, options.TargetBranch);

        if (diffs.Count == 0)
        {
            Console.WriteLine("No relevant changes found between branches. Nothing to review.");
            Console.WriteLine("[]");
            return 0;
        }

        // 3. Send diffs + rules to LLM for review
        var llmService = new LlmReviewService(gitHubToken, model, endpoint);
        var results = await llmService.ReviewAsync(diffs, rulesContent);

        // 4. Output results as structured JSON
        var jsonOutput = JsonSerializer.Serialize(results, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine("  REVIEW RESULTS");
        Console.WriteLine("═══════════════════════════════════════════");
        Console.WriteLine(jsonOutput);

        Console.WriteLine();
        Console.WriteLine($"Total violations found: {results.Count}");

        // 5. Write violations to a .txt report file
        var reportWriter = new ReportWriter();
        var reportPath = await reportWriter.WriteReportAsync(
            results, options.SourceBranch, options.TargetBranch);
        Console.WriteLine($"Report saved to: {reportPath}");

        return results.Count > 0 ? 1 : 0;
    }
    catch (FileNotFoundException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 2;
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 2;
    }
    catch (LibGit2Sharp.NotFoundException ex)
    {
        Console.Error.WriteLine($"Git error: {ex.Message}");
        return 2;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unexpected error: {ex.Message}");
        Console.Error.WriteLine(ex.StackTrace);
        return 3;
    }
}
