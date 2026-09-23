using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Worker;

/// <summary>
/// The agent's own memory: one memory.md file read before each task and
/// updated afterwards.
///
/// Memory belongs to the agent, not the repository it works in. All participants
/// work in the same repository; if the file lived there, every agent would read
/// and write the same memory, and pull requests would conflict.
///
/// Its location depends on where the worker runs:
///   - locally: agent/memory.md, next to system.md, so it can be inspected
///   - in Azure: a blob in the team's storage account. The job runs in a
///              disposable container, so a file on disk would not survive.
/// </summary>
internal sealed class Memory
{
    /// <summary>
    /// Maximum file size before the oldest entries are removed. Every character
    /// is sent with every run, so this is a cost, not just storage.
    /// Around 6000 characters is about 1500 tokens.
    /// </summary>
    public const int DefaultMaxChars = 6000;

    private const string Section = "Memory";
    private const string LegacySection = "Til hukommelsen";
    private const int MaxPoints = 3;
    private const int MaxCharsPerPoint = 220;

    private readonly Func<Task<(string Content, ETag? Version)>> _read;
    private readonly Func<string, ETag?, Task<bool>> _write;

    public string Description { get; }
    public string AgentName { get; }
    public int MaxChars { get; }

    private Memory(
        string description,
        string agentName,
        int maxChars,
        Func<Task<(string, ETag?)>> read,
        Func<string, ETag?, Task<bool>> write)
    {
        Description = description;
        AgentName = agentName;
        MaxChars = maxChars;
        _read = read;
        _write = write;
    }

    /// <summary>
    /// Selects storage from the environment. MEMORY_CONNECTION uses a blob;
    /// otherwise a file is used.
    /// </summary>
    public static Memory FromEnvironment(string agentDir)
    {
        var agentName = Environment.GetEnvironmentVariable("AGENT_NAME") ?? "Mini-Nils";
        var maxChars = int.TryParse(Environment.GetEnvironmentVariable("MEMORY_MAX_CHARS"), out var m) && m > 0
            ? m
            : DefaultMaxChars;

        var connection = Environment.GetEnvironmentVariable("MEMORY_CONNECTION");
        var path = Environment.GetEnvironmentVariable("MEMORY_PATH") is { Length: > 0 } s
            ? Path.GetFullPath(s)
            : Path.Combine(agentDir, "memory.md");

        if (!string.IsNullOrWhiteSpace(connection))
        {
            return BlobBacked(connection, agentName, maxChars, seed: path);
        }

        return FileBacked(path, agentName, maxChars);
    }

    private static Memory FileBacked(string path, string agentName, int maxChars) => new(
        $"file {path}",
        agentName,
        maxChars,
        read: async () => (File.Exists(path) ? await File.ReadAllTextAsync(path) : "", null),
        write: async (content, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
            return true;
        });

    /// <param name="seed">
    /// The file memory starts from when the blob does not exist yet. If
    /// agent/memory.md is committed to the repository, it is included in the
    /// image and the first Azure run starts with the locally learned context.
    /// </param>
    private static Memory BlobBacked(string connection, string agentName, int maxChars, string seed)
    {
        var container = new BlobContainerClient(connection, "hukommelse");
        var blob = container.GetBlobClient("memory.md");

        return new Memory(
            $"blob {container.AccountName}/hukommelse/memory.md",
            agentName,
            maxChars,
            read: async () =>
            {
                try
                {
                    var response = await blob.DownloadContentAsync();
                    return (response.Value.Content.ToString(), response.Value.Details.ETag);
                }
                catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
                {
                    // No blob exists yet. Start from the file included in the image, if present.
                    return (File.Exists(seed) ? await File.ReadAllTextAsync(seed) : "", null);
                }
            },
            write: async (content, version) =>
            {
                await container.CreateIfNotExistsAsync();

                // Up to three jobs can run concurrently. Write only if nobody
                // else has written since the read, or their entry would be lost.
                var conditions = version is { } e
                    ? new BlobRequestConditions { IfMatch = e }
                    : new BlobRequestConditions { IfNoneMatch = ETag.All };

                try
                {
                    await blob.UploadAsync(
                        BinaryData.FromString(content),
                        new BlobUploadOptions
                        {
                            Conditions = conditions,
                            HttpHeaders = new BlobHttpHeaders { ContentType = "text/markdown; charset=utf-8" },
                        });
                    return true;
                }
                catch (RequestFailedException ex) when (ex.Status is 409 or 412)
                {
                    return false;   // Another job won the race; retry.
                }
            });
    }

    /// <summary>Returns the current memory, or empty text.</summary>
    public async Task<string> Read() => (await _read()).Content;

    /// <summary>
    /// Adds an entry at the top and saves it. Retries if another job wrote
    /// concurrently. Returns false when there is nothing to write.
    /// </summary>
    public async Task<bool> Remember(AgentTask task, string outcome, IReadOnlyList<string> points)
    {
        if (points.Count == 0)
        {
            return false;
        }

        var heading = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} · #{task.IssueNumber} {Truncate(task.Title, 60)} · {outcome}");

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var (content, version) = await _read();
            var updated = AddEntry(content, AgentName, heading, points, MaxChars);

            if (await _write(updated, version))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
        }

        throw new InvalidOperationException("Could not write memory after five attempts.");
    }

    // ------------------------------------------------------------------------
    // Pure functions. No I/O, so they are easy to test.
    // ------------------------------------------------------------------------

    /// <summary>
    /// Extracts points under "## Memory" from the agent's final report.
    /// The legacy Norwegian heading remains readable for existing memories.
    /// At most three points are kept, and each point is truncated. An agent
    /// writing an essay to memory pays for it on every subsequent run.
    /// </summary>
    public static IReadOnlyList<string> ExtractContributions(string report)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            return [];
        }

        var match = Regex.Match(
            report,
            @"^\#{2,3}\s*(?:" + Regex.Escape(Section) + "|" + Regex.Escape(LegacySection) + @")\s*$(?<content>.*?)(?=^\#{1,3}\s|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return [];
        }

        return match.Groups["content"].Value
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('-') || l.StartsWith('*'))
            .Select(l => l.TrimStart('-', '*', ' ').Trim())
            .Where(l => l.Length > 0 && !l.Equals("Ingen", StringComparison.OrdinalIgnoreCase)
                                     && !l.Equals("Ingen.", StringComparison.OrdinalIgnoreCase))
            .Select(l => Truncate(l, MaxCharsPerPoint))
            .Take(MaxPoints)
            .ToList();
    }

    /// <summary>
    /// Adds a new entry at the top and removes the oldest entries until the
    /// file is below <paramref name="maxChars"/>. Entries are removed whole.
    /// </summary>
    public static string AddEntry(string existing, string agentName, string heading, IEnumerable<string> points, int maxChars)
    {
        var entries = GetEntries(existing);

        var entry = new StringBuilder();
        entry.Append("## ").AppendLine(heading);
        foreach (var point in points)
        {
            entry.Append("- ").AppendLine(point);
        }
        entries.Insert(0, entry.ToString().TrimEnd());

        var header = Header(agentName);
        while (entries.Count > 1 && SetContent(header, entries).Length > maxChars)
        {
            entries.RemoveAt(entries.Count - 1);
        }

        return SetContent(header, entries);
    }

    private static List<string> GetEntries(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        // Everything from the first "## " onward is an entry. The content
        // above it is the header, which is rewritten each time.
        var start = content.IndexOf("\n## ", StringComparison.Ordinal);
        if (content.StartsWith("## ", StringComparison.Ordinal))
        {
            start = 0;
        }
        else if (start < 0)
        {
            return [];
        }
        else
        {
            start += 1;
        }

        return Regex.Split(content[start..].Replace("\r\n", "\n"), @"(?m)^(?=## )")
            .Select(o => o.Trim())
            .Where(o => o.Length > 0)
            .ToList();
    }

    private static string Header(string agentName) =>
        $"# Memory - {agentName}\n\n" +
        "Durable facts learned from previous tasks, newest first. The worker adds\n" +
        "an entry after each task and removes the oldest entries when the file grows.\n" +
        "This file may be edited manually.\n";

    private static string SetContent(string header, List<string> entries) =>
        header + "\n" + string.Join("\n\n", entries) + "\n";

    private static string Truncate(string text, int max)
    {
        var normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= max ? normalized : normalized[..(max - 1)].TrimEnd() + "…";
    }
}
