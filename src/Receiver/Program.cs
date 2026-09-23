using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Storage.Queues;

// Mini-Nils receiver.
//
// Receives GitHub webhooks, verifies the signature, and queues a message when
// an issue receives the agent label or somebody mentions the agent on a PR.
// No LLM, git, or code: just HTTP in and a queue out.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var secret = Require("WEBHOOK_SECRET");
var queueConnection = Require("QUEUE_CONNECTION");
var queueName = Environment.GetEnvironmentVariable("QUEUE_NAME") ?? "agent-tasks";
var agentLabel = Environment.GetEnvironmentVariable("AGENT_LABEL") ?? "agent";
var agentMention = Environment.GetEnvironmentVariable("AGENT_MENTION")?.Trim().TrimStart('@') ?? "";
// The only repository the agent may work in. Everything else is ignored.
var targetRepo = Environment.GetEnvironmentVariable("TARGET_REPO") is { Length: > 0 } configuredRepo
    ? configuredRepo.Trim()
    : "novanet/workshop.oslo-live";

var queue = new QueueClient(queueConnection, queueName);
await queue.CreateIfNotExistsAsync();

app.MapGet("/", () =>
    $"Mini-Nils receiver. Listening for the «{agentLabel}» label" +
    (agentMention.Length > 0 ? $" and @{agentMention} mentions." : "."));

app.MapPost("/webhook", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();

    var signature = request.Headers["X-Hub-Signature-256"].ToString();
    if (!SignatureIsValid(secret, body, signature))
    {
        app.Logger.LogWarning("Webhook rejected: invalid signature");
        return (IResult)Results.Unauthorized();
    }

    using var doc = JsonDocument.Parse(body);
    var root = doc.RootElement;
    var eventName = request.Headers["X-GitHub-Event"].ToString();
    var deliveryId = request.Headers["X-GitHub-Delivery"].ToString();

    var payloadRepo = root.TryGetProperty("repository", out var repository) &&
                      repository.TryGetProperty("full_name", out var fullName)
        ? fullName.GetString()
        : null;
    if (payloadRepo is not null && !IsTargetRepo(payloadRepo))
    {
        app.Logger.LogWarning("Webhook ignored: {Repo} is not {TargetRepo}", payloadRepo, targetRepo);
        return (IResult)Results.Ok($"Ignored repository: «{payloadRepo}»");
    }

    if (eventName == "issue_comment")
    {
        return await HandleIssueComment(root, deliveryId);
    }

    if (eventName == "pull_request_review_comment")
    {
        return await HandleReviewComment(root, deliveryId);
    }

    if (eventName == "pull_request_review")
    {
        return await HandleReview(root, deliveryId);
    }

    if (eventName != "issues")
    {
        return (IResult)Results.Ok($"Ignored event: «{eventName}»");
    }

    var action = root.GetProperty("action").GetString();
    if (action != "labeled")
    {
        return (IResult)Results.Ok($"Ignored action: «{action}»");
    }

    var label = root.GetProperty("label").GetProperty("name").GetString();
    if (label != agentLabel)
    {
        return (IResult)Results.Ok($"Ignored label: «{label}»");
    }

    var repo = root.GetProperty("repository").GetProperty("full_name").GetString()!;
    var issueNumber = root.GetProperty("issue").GetProperty("number").GetInt32();

    await Enqueue(repo, issueNumber, kind: "issue", deliveryId: deliveryId);
    app.Logger.LogInformation("Task queued: {Repo}#{Issue}", repo, issueNumber);
    return (IResult)Results.Accepted(value: $"Queued: {repo}#{issueNumber}");
});

// Fallback when the webhook does not arrive: queue a task manually.
//   curl -X POST https://<receiver>/tasks -H "X-Webhook-Secret: ..." -H "Content-Type: application/json" \
//        -d '{"repo":"owner/name","issueNumber":3}'
app.MapPost("/tasks", async (HttpRequest request) =>
{
    if (request.Headers["X-Webhook-Secret"].ToString() != secret)
    {
        return (IResult)Results.Unauthorized();
    }

    var task = await request.ReadFromJsonAsync<ManualTask>();
    if (task is null || string.IsNullOrWhiteSpace(task.Repo) || task.IssueNumber <= 0)
    {
        return (IResult)Results.BadRequest("Expected {\"repo\":\"owner/name\",\"issueNumber\":3}");
    }

    if (!IsTargetRepo(task.Repo))
    {
        return (IResult)Results.BadRequest($"This agent only works in {targetRepo}.");
    }

    await Enqueue(task.Repo, task.IssueNumber, kind: "issue", deliveryId: "manual");
    return (IResult)Results.Accepted(value: $"Queued: {task.Repo}#{task.IssueNumber}");
});

app.Run();

async Task<IResult> HandleIssueComment(JsonElement root, string deliveryId)
{
    if (root.GetProperty("action").GetString() != "created")
    {
        return Results.Ok("Ignored issue comment action.");
    }

    var issue = root.GetProperty("issue");
    if (!issue.TryGetProperty("pull_request", out _))
    {
        return Results.Ok("Ignored comment on a normal issue.");
    }

    var comment = root.GetProperty("comment");
    if (!ShouldQueueMention(comment))
    {
        return Results.Ok("Ignored PR comment without an agent mention.");
    }

    // issue_comment payloads do not include the PR head; the worker checks for forks.
    if (!IsTrustedAuthor(comment))
    {
        return Results.Ok("Ignored mention from someone who is not a collaborator.");
    }

    var repo = root.GetProperty("repository").GetProperty("full_name").GetString()!;
    var pullRequest = issue.GetProperty("number").GetInt32();
    var commentId = comment.GetProperty("id").GetInt64();
    await Enqueue(repo, pullRequest, "pr-comment", commentId, pullRequest, deliveryId);
    app.Logger.LogInformation("PR comment mention queued: {Repo}#{PullRequest} comment {CommentId}",
        repo, pullRequest, commentId);
    return Results.Accepted(value: $"Queued PR mention: {repo}#{pullRequest}");
}

async Task<IResult> HandleReviewComment(JsonElement root, string deliveryId)
{
    if (root.GetProperty("action").GetString() != "created")
    {
        return Results.Ok("Ignored review comment action.");
    }

    var comment = root.GetProperty("comment");
    if (!ShouldQueueMention(comment))
    {
        return Results.Ok("Ignored review comment without an agent mention.");
    }

    if (!IsTrustedAuthor(comment) || IsFromFork(root))
    {
        return Results.Ok("Ignored mention from a non-collaborator or a fork.");
    }

    var repo = root.GetProperty("repository").GetProperty("full_name").GetString()!;
    var pullRequest = root.GetProperty("pull_request").GetProperty("number").GetInt32();
    var commentId = comment.GetProperty("id").GetInt64();
    await Enqueue(repo, pullRequest, "review-comment", commentId, pullRequest, deliveryId);
    app.Logger.LogInformation("Review comment mention queued: {Repo}#{PullRequest} comment {CommentId}",
        repo, pullRequest, commentId);
    return Results.Accepted(value: $"Queued review mention: {repo}#{pullRequest}");
}

async Task<IResult> HandleReview(JsonElement root, string deliveryId)
{
    if (root.GetProperty("action").GetString() != "submitted")
    {
        return Results.Ok("Ignored review action.");
    }

    var review = root.GetProperty("review");
    if (!ShouldQueueMention(review))
    {
        return Results.Ok("Ignored review without an agent mention.");
    }

    if (!IsTrustedAuthor(review) || IsFromFork(root))
    {
        return Results.Ok("Ignored mention from a non-collaborator or a fork.");
    }

    var repo = root.GetProperty("repository").GetProperty("full_name").GetString()!;
    var pullRequest = root.GetProperty("pull_request").GetProperty("number").GetInt32();
    var commentId = review.GetProperty("id").GetInt64();
    await Enqueue(repo, pullRequest, "review", commentId, pullRequest, deliveryId);
    app.Logger.LogInformation("Pull request review mention queued: {Repo}#{PullRequest} review {ReviewId}",
        repo, pullRequest, commentId);
    return Results.Accepted(value: $"Queued review mention: {repo}#{pullRequest}");
}

bool ShouldQueueMention(JsonElement comment)
{
    if (agentMention.Length == 0)
    {
        return false;
    }

    var author = comment.TryGetProperty("user", out var user)
        ? user.GetProperty("login").GetString()
        : null;
    if (string.Equals(author, agentMention, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var body = comment.GetProperty("body").GetString() ?? "";
    var pattern = $@"(?<![A-Za-z0-9_-])@{Regex.Escape(agentMention)}\b";
    return Regex.IsMatch(body, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

bool IsTargetRepo(string repo) =>
    string.Equals(repo.Trim(), targetRepo, StringComparison.OrdinalIgnoreCase);

// Mentions start a model run with the agent's token, so only people with
// write access to the repository may trigger one.
static bool IsTrustedAuthor(JsonElement comment)
{
    var association = comment.TryGetProperty("author_association", out var value)
        ? value.GetString()
        : null;
    return association is "OWNER" or "MEMBER" or "COLLABORATOR";
}

// A PR from a fork contains code the agent should not check out and run tools on.
// Returns false when the payload does not say; the worker checks again.
static bool IsFromFork(JsonElement root)
{
    if (!root.TryGetProperty("pull_request", out var pr) ||
        !pr.TryGetProperty("head", out var head) ||
        !head.TryGetProperty("repo", out var headRepo) ||
        !pr.TryGetProperty("base", out var @base) ||
        !@base.TryGetProperty("repo", out var baseRepo) ||
        baseRepo.ValueKind != JsonValueKind.Object)
    {
        return false;
    }

    // A deleted fork shows up as a null head repository.
    if (headRepo.ValueKind != JsonValueKind.Object)
    {
        return true;
    }

    var headName = headRepo.TryGetProperty("full_name", out var h) ? h.GetString() : null;
    var baseName = baseRepo.TryGetProperty("full_name", out var b) ? b.GetString() : null;
    return !string.Equals(headName, baseName, StringComparison.OrdinalIgnoreCase);
}

async Task Enqueue(
    string repo,
    int issueNumber,
    string kind,
    long? commentId = null,
    int? pullRequestNumber = null,
    string? deliveryId = null)
{
    var message = JsonSerializer.Serialize(new
    {
        repo,
        issueNumber,
        kind,
        commentId,
        pullRequestNumber,
        deliveryId,
    });
    await queue.SendMessageAsync(message);
}

static bool SignatureIsValid(string secret, string body, string signatureHeader)
{
    if (!signatureHeader.StartsWith("sha256=", StringComparison.Ordinal))
    {
        return false;
    }

    var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
    byte[] actual;
    try
    {
        actual = Convert.FromHexString(signatureHeader["sha256=".Length..]);
    }
    catch (FormatException)
    {
        return false;
    }

    return CryptographicOperations.FixedTimeEquals(expected, actual);
}

static string Require(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Environment variable {name} is missing. See .env.example.");

sealed record ManualTask(string Repo, int IssueNumber);
