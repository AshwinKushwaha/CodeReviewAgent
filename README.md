# CodeReviewAgent

An AI-powered code review tool that analyses **only the changed lines** between two Git branches against a rules document, and outputs structured violation reports.

---

## Prerequisites

| Requirement | Minimum Version | Download |
|---|---|---|
| .NET SDK | 10.0 | https://dotnet.microsoft.com/download/dotnet/10.0 |
| Git | Any modern version | https://git-scm.com/downloads |
| GitHub Account | With Copilot access | https://github.com |

---

## Quick Setup (Recommended)

After cloning, run the setup script once from the **repo root**:

```powershell
.\setup.ps1
```

The script will:
1. Verify .NET 10 and Git are installed
2. Create `appsettings.json` from the committed template
3. Prompt you to paste your **GitHub Personal Access Token**
4. Restore NuGet packages
5. Build the project

---

## Manual Setup

If you prefer to set up manually:

### 1. Create `appsettings.json`

Copy the template and fill in your token:

```powershell
Copy-Item CodeReviewAgent\appsettings.template.json CodeReviewAgent\appsettings.json
```

Edit `CodeReviewAgent\appsettings.json`:

```json
{
  "GitHubToken": "ghp_YourTokenHere",
  "GitHubModelsEndpoint": "https://models.github.ai/inference",
  "Model": "openai/gpt-4.1"
}
```

> ?? `appsettings.json` is listed in `.gitignore` and will **never be committed**.

### 2. Get a GitHub Personal Access Token

1. Go to [github.com/settings/tokens](https://github.com/settings/tokens)
2. Click **Generate new token (classic)**
3. Give it a name, e.g. `CodeReviewAgent`
4. Ensure your GitHub account has an active **GitHub Copilot** subscription
5. Copy the token and paste it into `appsettings.json`

### 3. Restore and build

```powershell
cd CodeReviewAgent
dotnet restore
dotnet build
```

---

## Running a Review

First, **clone the target repository locally** (the agent works on local Git repos):

```powershell
git clone https://github.com/your-org/your-repo "C:\Repos\your-repo"
```

Then run from the `CodeReviewAgent` folder:

```powershell
cd CodeReviewAgent

dotnet run -- `
  --repoPath    "C:\Repos\your-repo" `
  --sourceBranch "feature/my-feature" `
  --targetBranch "main" `
  --rulesPath    ".\rules.md"
```

### Arguments

| Argument | Description |
|---|---|
| `--repoPath` | Full local path to the cloned Git repository |
| `--sourceBranch` | Branch containing the new changes (merge **from**) |
| `--targetBranch` | Base branch to compare against (merge **into**) |
| `--rulesPath` | Path to the rules document (`.md` or `.txt`) |

---

## Output

### Console

```
???????????????????????????????????????????
  CodeReviewAgent – AI Code Review
???????????????????????????????????????????

Using model: openai/gpt-4.1
Endpoint:    https://models.github.ai/inference

Loaded rules document: .\rules.md (5979 chars)
Comparing main (97e77af0) ? feature/my-feature (5305fe30)
Found 8 changed file(s) with relevant diffs.
Sending 12 chunk(s) to LLM for review...
  Processing chunk 1...
  Processing chunk 2...
  ...

[
  {
    "file": "Services/UserService.cs",
    "line": 42,
    "ruleViolated": "RULE-STR-003: String Equality Comparison",
    "explanation": "Direct == comparison used on a string.",
    "suggestedFix": "Use string.Equals(..., StringComparison.InvariantCultureIgnoreCase)"
  }
]

Total violations found: 1
Report saved to: C:\...\CodeReviewAgent\ReviewReports\review_2026-02-25_14-30-00.txt
```

### Report File

A timestamped `.txt` report is saved to `CodeReviewAgent\ReviewReports\` after every run.
This folder is excluded from Git via `.gitignore`.

---

## Configuring the Model

Edit `appsettings.json` to change the AI model or endpoint:

```json
{
  "GitHubToken": "ghp_...",
  "GitHubModelsEndpoint": "https://models.github.ai/inference",
  "Model": "openai/gpt-4.1"
}
```

Available models on GitHub Models: [github.com/marketplace/models](https://github.com/marketplace/models)

---

## Exit Codes

| Code | Meaning |
|---|---|
| `0` | Review complete — no violations found |
| `1` | Review complete — violations found |
| `2` | Configuration or input error |
| `3` | Unexpected runtime error |

---

## Project Structure

```
CodeReviewAgent/
??? Program.cs                    # Entry point + CLI + orchestration
??? appsettings.json              # Local config (gitignored)
??? appsettings.template.json     # Committed safe template
??? rules.md                      # Coding rules document
??? ReviewReports/                # Generated reports (gitignored)
??? Models/
?   ??? CommandLineOptions.cs
?   ??? DiffLine.cs
?   ??? FileDiff.cs
?   ??? ReviewResult.cs
??? Services/
    ??? GitDiffService.cs         # Extracts diffs via LibGit2Sharp
    ??? RulesLoader.cs            # Reads the rules document
    ??? LlmReviewService.cs       # Calls GitHub Models (OpenAI SDK)
    ??? ReportWriter.cs           # Writes the .txt report file
```

---

## Rate Limits

GitHub Models free tier allows **10 requests per minute**. The agent handles this automatically:
- Proactive pacing keeps requests under the limit
- Automatic retry with exponential backoff on `429` responses
- Large diffs are split into smaller chunks and re-queued on `413` responses
