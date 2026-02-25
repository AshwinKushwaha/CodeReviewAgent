namespace CodeReviewAgent.Services;

/// <summary>
/// Loads and validates the rules document from disk.
/// </summary>
public sealed class RulesLoader
{
    /// <summary>
    /// Reads the rules document from the given file path.
    /// Supports .txt and .md files.
    /// </summary>
    public async Task<string> LoadRulesAsync(string rulesPath)
    {
        if (!File.Exists(rulesPath))
            throw new FileNotFoundException($"Rules document not found at: {rulesPath}");

        var extension = Path.GetExtension(rulesPath).ToLowerInvariant();
        if (extension is not ".txt" and not ".md")
            throw new InvalidOperationException($"Unsupported rules file format '{extension}'. Use .txt or .md.");

        var content = await File.ReadAllTextAsync(rulesPath);

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("Rules document is empty.");

        Console.WriteLine($"Loaded rules document: {rulesPath} ({content.Length} chars)");
        return content;
    }
}
