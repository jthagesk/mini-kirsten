using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Worker;

/// <summary>
/// Git and GitHub operations for one checked-out repository. Uses the git and
/// gh CLIs, authenticated with GH_TOKEN from the environment.
/// </summary>
internal sealed class Repository
{
    private readonly string _repo;

    private Repository(string repo, string path)
    {
        _repo = repo;
        Path = path;
    }

    public string Path { get; }

    public static async Task<Repository> Clone(string repo, string workspace)
    {
        // Let git use gh authentication so the token never appears in a URL.
        Log.Info("Konfigurerer GitHub-autentisering for git.");
        await Proc.Must("gh", ["auth", "setup-git"]);

        var name = repo.Split('/')[^1];
        var path = System.IO.Path.Combine(workspace, name);

        if (Directory.Exists(System.IO.Path.Combine(path, ".git")))
        {
            Log.Info($"Repoet finnes allerede i {path}, henter siste endringer");
            Log.Info("Henter origin/main.");
            await Proc.Must("git", ["fetch", "--prune", "origin"], path);
            Log.Info("Sjekker ut origin/main.");
            await Proc.Must("git", ["checkout", "--force", "main"], path);
            await Proc.Must("git", ["reset", "--hard", "origin/main"], path);
            // Do not carry uncommitted files from an earlier run into the next PR.
            Log.Info("Fjerner lokale filer fra forrige kjøring.");
            await Proc.Must("git", ["clean", "-fd"], path);
        }
        else
        {
            Directory.CreateDirectory(workspace);
            Log.Info($"Kloner repoet til {path}.");
            await Proc.Must("git", ["clone", "--quiet", $"https://github.com/{repo}.git", path]);
        }

        Log.Info("Konfigurerer git-identitet.");
        await Proc.Must("git", ["config", "user.name", Environment.GetEnvironmentVariable("AGENT_NAME") ?? "Mini-Nils"], path);
        await Proc.Must("git", ["config", "user.email", "agent@example.invalid"], path);

        return new Repository(repo, path);
    }

    public async Task CreateBranch(string branch)
    {
        // Delete an old local branch with the same name so a new run starts from main.
        Log.Info($"Fjerner eventuell lokal branch {branch}.");
        await Proc.Run("git", ["branch", "-D", branch], Path);
        Log.Info($"Sjekker ut ny branch {branch}.");
        await Proc.Must("git", ["checkout", "-b", branch], Path);
    }

    /// <summary>
    /// True when the pull request comes from a fork (or a deleted fork). The
    /// agent does not check out or run tools on code from outside the repository.
    /// </summary>
    public async Task<bool> IsPullRequestFromFork(int number)
    {
        var result = await Proc.Must(
            "gh",
            ["api", $"repos/{_repo}/pulls/{number}", "--jq", ".head.repo.full_name // \"\""],
            Path,
            timeout: TimeSpan.FromSeconds(30));
        return !string.Equals(result.StdOut.Trim(), _repo, StringComparison.OrdinalIgnoreCase);
    }

    public async Task CheckoutPullRequest(int number)
    {
        Log.Info($"Henter pull request #{number}.");
        await Proc.Must("git", ["fetch", "origin", $"pull/{number}/head:refs/remotes/origin/pr/{number}"], Path);
        await Proc.Must("git", ["checkout", "--detach", $"refs/remotes/origin/pr/{number}"], Path);
    }

    public async Task<bool> HasChanges()
    {
        Log.Info("Kjører git status.");
        var status = await Proc.Must("git", ["status", "--porcelain"], Path);
        return !string.IsNullOrWhiteSpace(status.StdOut);
    }

    public async Task<string> Diff()
    {
        await Proc.Must("git", ["add", "-A"], Path);
        var diff = await Proc.Must("git", ["diff", "--cached"], Path);
        return diff.StdOut;
    }

    public async Task CommitAndPush(string branch, string message)
    {
        Log.Info("Stager endringene.");
        await Proc.Must("git", ["add", "-A"], Path);
        Log.Info("Oppretter commit.");
        await Proc.Must("git", ["commit", "--quiet", "-m", message], Path);
        Log.Info($"Pusher {branch}.");
        await Proc.Must("git", ["push", "--force-with-lease", "--set-upstream", "origin", branch], Path);
        Log.Info("Push ferdig.");
    }

    private static readonly Regex MetricsLine = new(@"<!--\s*mini-nils\s*(\{.*?\})\s*-->", RegexOptions.Singleline);

    /// <summary>
    /// Adds turns, tokens, cost and seconds from the metrics line in
    /// <paramref name="previousBody"/> to the one in <paramref name="newBody"/>,
    /// and counts the runs. The result board shows the PR's total cost.
    /// </summary>
    internal static string AccumulateMetrics(string previousBody, string newBody)
    {
        var old = MetricsLine.Match(previousBody);
        var current = MetricsLine.Match(newBody);
        if (!old.Success || !current.Success)
        {
            return newBody;
        }

        JsonObject? before;
        JsonObject? now;
        try
        {
            before = JsonNode.Parse(old.Groups[1].Value) as JsonObject;
            now = JsonNode.Parse(current.Groups[1].Value) as JsonObject;
        }
        catch (JsonException)
        {
            return newBody;
        }

        if (before is null || now is null)
        {
            return newBody;
        }

        decimal Number(JsonObject o, string name) =>
            o[name] is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : 0m;

        now["turns"] = (int)(Number(before, "turns") + Number(now, "turns"));
        now["tokens"] = (long)(Number(before, "tokens") + Number(now, "tokens"));
        now["costUsd"] = Math.Round(Number(before, "costUsd") + Number(now, "costUsd"), 4);
        now["seconds"] = (int)(Number(before, "seconds") + Number(now, "seconds"));
        now["runs"] = (int)Math.Max(1m, Number(before, "runs")) + 1;

        var line = $"<!-- mini-nils {now.ToJsonString()} -->";
        var note = string.Create(CultureInfo.InvariantCulture,
            $"_Alle {now["runs"]} kjøringer på denne PR-en har kostet ${Number(now, "costUsd"):0.000} til sammen._");
        return newBody[..current.Index] + note + "\n\n" + line + newBody[(current.Index + current.Length)..];
    }

    public async Task<string> CreatePullRequest(string branch, string title, string body, bool draft)
    {
        // If a PR already exists for the branch (a new run for the same issue),
        // update it. Only an open PR is reused; merged or closed PRs are not edited.
        Log.Info("Sjekker om det allerede finnes en åpen pull request.");
        var existing = await Proc.Run(
            "gh",
            ["pr", "view", branch, "--repo", _repo, "--json", "url,state", "--jq", "select(.state == \"OPEN\") | .url"],
            Path,
            timeout: TimeSpan.FromSeconds(30));
        if (existing.Ok && existing.StdOut.Trim().StartsWith("http", StringComparison.Ordinal))
        {
            Log.Info("Oppdaterer eksisterende pull request.");
            // The new body would replace the earlier run's cost. Add it instead,
            // so the PR shows what all runs on it cost together.
            var previous = await Proc.Run(
                "gh",
                ["pr", "view", branch, "--repo", _repo, "--json", "body", "--jq", ".body"],
                Path,
                timeout: TimeSpan.FromSeconds(30));
            if (previous.Ok)
            {
                body = AccumulateMetrics(previous.StdOut, body);
            }

            await Proc.Must(
                "gh",
                ["pr", "edit", branch, "--repo", _repo, "--title", title, "--body", body],
                Path,
                timeout: TimeSpan.FromMinutes(2));
            return existing.StdOut.Trim();
        }

        Log.Info(draft ? "Oppretter pull request som utkast." : "Oppretter pull request.");
        List<string> args = ["pr", "create", "--repo", _repo, "--head", branch, "--base", "main", "--title", title, "--body", body];
        if (draft)
        {
            args.Add("--draft");
        }

        var result = await Proc.Must("gh", args, Path, timeout: TimeSpan.FromMinutes(2));
        return result.StdOut.Trim().Split('\n')[^1];
    }

    public Task CommentOnIssue(int issueNumber, string body) =>
        Proc.Must("gh", ["issue", "comment", issueNumber.ToString(), "--repo", _repo, "--body", body], Path);

    public Task ReplyToInteraction(AgentInteraction interaction, string body)
    {
        if (interaction.IsReviewThread)
        {
            return Proc.Must(
                "gh",
                [
                    "api",
                    $"repos/{_repo}/pulls/{interaction.PullRequestNumber}/comments/{interaction.CommentId}/replies",
                    "--method", "POST",
                    // --raw-field sends the text as is. --field would read "@file" and parse numbers.
                    "--raw-field", $"body={body}",
                ],
                Path);
        }

        return Proc.Must(
            "gh",
            [
                "pr", "comment", interaction.PullRequestNumber.ToString(),
                "--repo", _repo,
                "--body", body,
            ],
            Path);
    }
}
