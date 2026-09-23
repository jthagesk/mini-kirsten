using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Queues;

namespace Worker;

/// <summary>
/// One agent task: an issue in a GitHub repository.
/// </summary>
internal sealed record AgentTask(
    string Repo,
    int IssueNumber,
    string Title,
    string Body,
    AgentInteraction? Interaction = null)
{
    public string Slug
    {
        get
        {
            var ascii = Title.ToLowerInvariant()
                .Replace('æ', 'a').Replace('ø', 'o').Replace('å', 'a');
            var chars = ascii.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
            var slug = new string(chars).Trim('-');
            while (slug.Contains("--")) slug = slug.Replace("--", "-");
            return slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
        }
    }

    public string BranchName
    {
        get
        {
            var agent = Environment.GetEnvironmentVariable("AGENT_NAME") ?? "Mini-Nils";
            var ascii = agent.ToLowerInvariant()
                .Replace('æ', 'a').Replace('ø', 'o').Replace('å', 'a');
            var chars = ascii.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
            var agentSlug = new string(chars).Trim('-');
            while (agentSlug.Contains("--")) agentSlug = agentSlug.Replace("--", "-");
            if (string.IsNullOrEmpty(agentSlug)) agentSlug = "mini-nils";
            if (agentSlug.Length > 30) agentSlug = agentSlug[..30].TrimEnd('-');

            return $"agent/{agentSlug}/{IssueNumber}-{Slug}";
        }
    }
}

/// <summary>
/// Message placed in the queue by the receiver. The worker fetches the title and
/// body from GitHub, so the message is only a pointer.
/// </summary>
internal sealed record QueuedTask(
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("issueNumber")] int IssueNumber,
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("commentId")] long? CommentId = null,
    [property: JsonPropertyName("pullRequestNumber")] int? PullRequestNumber = null,
    [property: JsonPropertyName("deliveryId")] string? DeliveryId = null);

internal sealed record AgentInteraction(
    string Kind,
    int PullRequestNumber,
    long CommentId,
    string Body,
    string? Url,
    string? DeliveryId,
    string? AuthorAssociation = null)
{
    public bool IsReviewThread => Kind == "review-comment";

    /// <summary>Only people with write access to the repository may start a mention run.</summary>
    public bool IsFromCollaborator => AuthorAssociation is "OWNER" or "MEMBER" or "COLLABORATOR";
}

internal static class TaskSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A message that has failed this many times is removed instead of retried.</summary>
    private const int MaxDequeueCount = 3;

    /// <summary>The only repository the agent may work in. Set by main.bicep.</summary>
    public static string TargetRepo =>
        Environment.GetEnvironmentVariable("TARGET_REPO") is { Length: > 0 } repo
            ? repo.Trim()
            : "novanet/workshop.oslo-live";

    /// <summary>Fetches issue details from GitHub through the gh CLI.</summary>
    public static async Task<AgentTask> FromIssue(string repo, int issueNumber)
    {
        var result = await Proc.Must("gh",
            ["issue", "view", issueNumber.ToString(), "--repo", repo, "--json", "number,title,body"],
            timeout: TimeSpan.FromSeconds(30));

        using var doc = JsonDocument.Parse(result.StdOut);
        var root = doc.RootElement;
        var task = new AgentTask(
            repo,
            root.GetProperty("number").GetInt32(),
            root.GetProperty("title").GetString() ?? "",
            root.GetProperty("body").GetString() ?? "");
        Log.Info($"Issue #{task.IssueNumber} lest: «{task.Title}» ({task.Body.Length} tegn)");
        return task;
    }

    /// <summary>
    /// Fetches one queue message. Returns null when the queue is empty or the
    /// message was rejected, so the worker exits quietly. The message is deleted
    /// only after the task finishes, so a crash makes it visible again. Messages
    /// for another repository, and messages that keep failing, are deleted here.
    /// </summary>
    public static async Task<(AgentTask Task, Func<Task> Complete)?> FromQueue(string connection, string queueName)
    {
        Log.Info($"Åpner køen «{queueName}»");
        var client = new QueueClient(connection, queueName);
        await client.CreateIfNotExistsAsync();

        // Longer than the job can run (replicaTimeout in main.bicep), otherwise
        // the message can reappear during a long run and start another job.
        var response = await client.ReceiveMessageAsync(visibilityTimeout: TimeSpan.FromMinutes(90));
        var message = response.Value;
        if (message is null)
        {
            return null;
        }

        if (message.DequeueCount > MaxDequeueCount)
        {
            Log.Error($"Kømeldingen er hentet {message.DequeueCount} ganger uten å bli ferdig. Sletter den: {message.MessageText}");
            await client.DeleteMessageAsync(message.MessageId, message.PopReceipt);
            return null;
        }

        Log.Info("Kømelding mottatt. Leser innholdet.");
        var queued = JsonSerializer.Deserialize<QueuedTask>(message.MessageText, Json)
            ?? throw new InvalidOperationException($"Ugyldig melding i køen: {message.MessageText}");

        if (string.IsNullOrWhiteSpace(queued.Repo) ||
            !string.Equals(queued.Repo.Trim(), TargetRepo, StringComparison.OrdinalIgnoreCase))
        {
            Log.Error($"Avviser oppgave for {queued.Repo}. Agenten jobber bare i {TargetRepo}. Sletter meldingen.");
            await client.DeleteMessageAsync(message.MessageId, message.PopReceipt);
            return null;
        }

        var task = await FromIssue(queued.Repo, queued.IssueNumber);
        if (queued.CommentId is { } commentId &&
            queued.PullRequestNumber is { } pullRequestNumber &&
            queued.Kind is "pr-comment" or "review-comment" or "review")
        {
            var interaction = await FromComment(
                queued.Repo,
                queued.Kind,
                pullRequestNumber,
                commentId,
                queued.DeliveryId);
            task = task with { Interaction = interaction };
        }

        return (task, () => client.DeleteMessageAsync(message.MessageId, message.PopReceipt));
    }

    private static async Task<AgentInteraction> FromComment(
        string repo,
        string kind,
        int pullRequestNumber,
        long commentId,
        string? deliveryId)
    {
        var endpoint = kind == "review-comment"
            ? $"repos/{repo}/pulls/comments/{commentId}"
            : kind == "review"
                ? $"repos/{repo}/pulls/{pullRequestNumber}/reviews/{commentId}"
                : $"repos/{repo}/issues/comments/{commentId}";
        var result = await Proc.Must("gh", ["api", endpoint], timeout: TimeSpan.FromSeconds(30));
        using var doc = JsonDocument.Parse(result.StdOut);
        var root = doc.RootElement;
        return new AgentInteraction(
            kind,
            pullRequestNumber,
            commentId,
            root.GetProperty("body").GetString() ?? "",
            root.TryGetProperty("html_url", out var url) ? url.GetString() : null,
            deliveryId,
            root.TryGetProperty("author_association", out var association) ? association.GetString() : null);
    }
}
