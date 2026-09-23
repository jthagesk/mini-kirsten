using Worker;

// Mini-Nils worker.
//
//   dotnet run -- --issue 1 --repo ... --dry-run                 (read-only setup check)
//   dotnet run                                                    (Lab 2: fetch one queue message, then exit)
//
// Environment variables: see ../../.env.example

const string DefaultAnthropicWorkspaceId = "wrkspc_017LSdCTfnAddrQcuKrM38Aq";
const string AnthropicWorkspaceHeader = "anthropic-workspace-id";

try
{
    LoadEnvironment();
    ConfigureAnthropicWorkspace();
    var (issue, repo, skipReservation, dryRun) = ParseArgs(args);

    Require("GH_TOKEN");
    if (!dryRun)
    {
        Require("ANTHROPIC_API_KEY");
    }

    var agentDir = ResolveAgentDir();
    var workspace = Environment.GetEnvironmentVariable("WORKSPACE")
        ?? Path.Combine(Path.GetTempPath(), "mini-nils");
    Directory.CreateDirectory(workspace);

    var config = AgentConfig.Load(agentDir);
    Log.Info($"Agentmappe: {agentDir}");
    Log.Info($"Arbeidsområde: {workspace}");
    Log.Info($"Stager: {string.Join(" → ", config.Stages.Select(s => s.Name))}");
    Log.Info($"Anthropic workspace-ID: {(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_WORKSPACE_ID")) ? "ikke satt" : "satt")}");

    if (issue is not null && repo is not null && !dryRun)
    {
        Log.Step("Sjekker Anthropic API før lokal kjøring stoppes.");
        await ClaudeRunner.CheckApi(Environment.CurrentDirectory);
        throw new InvalidOperationException(
            "Lokal issue-kjøring er skrivebeskyttet i kurset. Bruk --dry-run før deploy; "
            + "først etter at webhooken er koblet til skal workeren kjøre fra Azure-køen.");
    }

    var pipeline = new Pipeline(config, agentDir, workspace);

    if (issue is { } n && repo is not null)
    {
        Log.Info($"Henter issue {repo}#{n} fra GitHub");
        var task = await TaskSource.FromIssue(repo, n);
        Log.Info("Issue hentet. Starter pipeline.");
        return await pipeline.Run(task, skipReservation, dryRun);
    }

    var connection = Require("QUEUE_CONNECTION");
    var queueName = Environment.GetEnvironmentVariable("QUEUE_NAME") ?? "agent-tasks";
    Log.Info($"Henter én melding fra køen «{queueName}»");

    var queued = await TaskSource.FromQueue(connection, queueName);
    if (queued is null)
    {
        Log.Info("Ingen oppgave å behandle. Avslutter.");
        return 0;
    }

    var (queuedTask, complete) = queued.Value;
    Log.Info($"Melding hentet: {queuedTask.Repo}#{queuedTask.IssueNumber}");
    try
    {
        return await pipeline.Run(queuedTask);
    }
    finally
    {
        // Delete it regardless of the outcome. The job has replicaRetryLimit 0,
        // and a hidden message would make KEDA start empty runs for an hour.
        await complete();
        Log.Info("Meldingen er slettet fra køen.");
    }
}
catch (Exception ex)
{
    Log.Error(ex.Message);
    return 1;
}

static (int? Issue, string? Repo, bool SkipReservation, bool DryRun) ParseArgs(string[] args)
{
    int? issue = null;
    string? repo = null;
    var skipReservation = false;
    var dryRun = false;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--issue" when i + 1 < args.Length:
                issue = int.Parse(args[++i]);
                break;
            case "--repo" when i + 1 < args.Length:
                repo = args[++i];
                break;
            case "--skip-reservation":
                skipReservation = true;
                break;
            case "--dry-run":
                dryRun = true;
                break;
            case "--help" or "-h":
                Console.WriteLine("Bruk: Worker [--issue <nr> --repo <owner/navn> --dry-run]");
                Environment.Exit(0);
                break;
        }
    }

    if ((issue is null) != (repo is null))
    {
        throw new ArgumentException("--issue og --repo må brukes sammen.");
    }

    if (dryRun && issue is null)
    {
        throw new ArgumentException("--dry-run krever --issue og --repo, slik at en kømelding ikke slettes uten å bli behandlet.");
    }

    return (issue, repo, skipReservation, dryRun);
}

/// <summary>
/// Loads .env from the working directory or one of its parents, so Windows
/// users without bash do not need "source .env". Existing variables win.
/// </summary>
static void LoadEnvironment()
{
    var dir = Directory.GetCurrentDirectory();
    for (var i = 0; i < 4 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
    {
        var file = Path.Combine(dir, ".env");
        if (!File.Exists(file)) continue;
        foreach (var line in File.ReadAllLines(file))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#') || !t.Contains('=')) continue;
            var name = t[..t.IndexOf('=')].Trim();
            var value = t[(t.IndexOf('=') + 1)..].Trim().Trim('"', '\'');
            if (value.Length > 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
        Log.Info($"Leste {file}");
        return;
    }
}

static void ConfigureAnthropicWorkspace()
{
    var workspaceId = Environment.GetEnvironmentVariable("ANTHROPIC_WORKSPACE_ID");
    if (string.IsNullOrWhiteSpace(workspaceId))
    {
        workspaceId = DefaultAnthropicWorkspaceId;
        Environment.SetEnvironmentVariable("ANTHROPIC_WORKSPACE_ID", workspaceId);
        Log.Info("ANTHROPIC_WORKSPACE_ID manglet; bruker kursets standard-workspace.");
    }

    var customHeaders = Environment.GetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS");
    if (!HasHeader(customHeaders, AnthropicWorkspaceHeader))
    {
        var workspaceHeader = $"{AnthropicWorkspaceHeader}: {workspaceId}";
        var value = string.IsNullOrWhiteSpace(customHeaders)
            ? workspaceHeader
            : $"{customHeaders.TrimEnd()}\n{workspaceHeader}";
        Environment.SetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS", value);
        Log.Info("Anthropic workspace-header er satt.");
    }
}

static bool HasHeader(string? headers, string name) =>
    headers?
        .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
        .Any(line => line.TrimStart().StartsWith($"{name}:", StringComparison.OrdinalIgnoreCase))
    ?? false;

static string Require(string name)
{
    if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value &&
        !IsExampleValue(name, value))
    {
        return value;
    }

    var hint = name switch
    {
        "ANTHROPIC_API_KEY" =>
            "Claude Code kan ikke starte uten denne nøkkelen. Be kurslederen om en nøkkel og legg den i .env som ANTHROPIC_API_KEY=sk-ant-... (ikke eksempelverdien).",
        "GH_TOKEN" =>
            "Workeren trenger et klassisk GitHub-token med public_repo, og du må være collaborator på novanet/workshop.oslo-live. Legg den virkelige verdien i .env som GH_TOKEN=ghp_... (ikke eksempelverdien).",
        "QUEUE_CONNECTION" =>
            "Kømodus krever Azure Storage-tilkoblingen i .env. Bruk --issue og --repo for lokal kjøring.",
        _ => "Legg variabelen i .env eller sett den i miljøet."
    };

    throw new InvalidOperationException($"Mangler eller ikke utfylt: {name}. {hint} Se .env.example.");
}

static bool IsExampleValue(string name, string value) =>
    name switch
    {
        "ANTHROPIC_API_KEY" => value is "sk-ant-..." or "sk-ant-…",
        "GH_TOKEN" => value is "ghp_..." or "ghp_…" or "github_pat_..." or "github_pat_…",
        _ => false
    };

static string ResolveAgentDir()
{
    var configured = Environment.GetEnvironmentVariable("AGENT_DIR");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return Path.GetFullPath(configured);
    }

    // Search upward from the working directory and application directory for agent/stages.json.
    foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "agent", "stages.json");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }
            dir = dir.Parent;
        }
    }

    throw new InvalidOperationException("Fant ikke agent/stages.json. Kjør fra starter-kit-mappen eller sett AGENT_DIR.");
}
