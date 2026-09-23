using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Worker;

internal sealed record ClaudeResult(
    bool Ok,
    string Text,
    string SessionId,
    int Turns,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    decimal CostUsd,
    TimeSpan Duration,
    bool CostEstimated = false)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

/// <summary>
/// Runs Claude Code headlessly (claude -p) in a checked-out repository. This is
/// the engine: the harness provides tools (Read, Edit, Bash, ...), while we
/// provide the prompt, system prompt, and allowlist.
/// </summary>
internal static class ClaudeRunner
{
    private const string PreferredApiCheckModel = "claude-haiku-4-5";

    /// <summary>
    /// List prices in USD per million tokens: input, output, cache read, cache write (5 min).
    /// Used only when Claude Code does not report a cost itself: a stage that is
    /// stopped by the time limit, or a model Claude Code has no price for.
    /// Normally total_cost_usd from Claude Code is used. Update when prices change.
    /// </summary>
    private static readonly (string Model, decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite)[] Prices =
    [
        ("claude-opus-5-5", 4m, 20m, 0.20m, 5m),
        ("claude-sonnet-5", 2m, 10m, 0.20m, 2.5m),
        ("claude-haiku-4-5", 1m, 5m, 0.10m, 1.25m),
    ];

    public static async Task CheckApi(string workingDir)
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Anthropic API-sjekk feilet: ANTHROPIC_API_KEY mangler.");
        }

        var baseUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL")
            ?.TrimEnd('/')
            ?? "https://api.anthropic.com";
        var modelsPath = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{baseUrl}/models?limit=100"
            : $"{baseUrl}/v1/models?limit=100";
        var workspaceId = Environment.GetEnvironmentVariable("ANTHROPIC_WORKSPACE_ID");

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, modelsPath);
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        if (!string.IsNullOrWhiteSpace(workspaceId))
        {
            request.Headers.TryAddWithoutValidation("anthropic-workspace-id", workspaceId);
        }

        Log.Info("Tester Anthropic API direkte uten modellkall.");
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var detail = body[..Math.Min(body.Length, 500)].Trim();
            throw new InvalidOperationException(
                $"Anthropic API-sjekk feilet (HTTP {(int)response.StatusCode}): {detail}");
        }

        Log.Info("Anthropic API svarer. Nøkkel og workspace-header er godkjent.");
        var apiCheckModel = SelectCheapestAvailableModel(body);
        Log.Info($"Fant tilgjengelig modell for kontrollping: {apiCheckModel}");

        var stage = new Stage
        {
            Name = "api-check",
            MaxTurns = 1,
            TimeoutMinutes = 2,
        };
        Log.Info($"Sender en kort Claude Code-ping med {apiCheckModel}.");
        var result = await Run(
            stage,
            apiCheckModel,
            "Svar bare med API_OK. Ikke bruk verktøy.",
            "Dette er en kort tilkoblingstest. Svar bare med API_OK.",
            workingDir);
        if (!result.Ok)
        {
            var detail = string.IsNullOrWhiteSpace(result.Text)
                ? "Claude Code returnerte ingen forklaring."
                : result.Text[..Math.Min(result.Text.Length, 500)].Trim();
            throw new InvalidOperationException($"Claude Code/Haiku-sjekk feilet: {detail}");
        }

        Log.Info($"Claude Code svarer med {apiCheckModel}.");
    }

    private static string SelectCheapestAvailableModel(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Anthropic API svarte uten en modelliste. Kan ikke velge kontrollmodell.");
        }

        var models = data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();

        // Prefer the course's Haiku model; fall back to any Haiku or Sonnet model.
        var model = models.FirstOrDefault(id =>
                       id.Equals(PreferredApiCheckModel, StringComparison.OrdinalIgnoreCase))
                   ?? models.FirstOrDefault(id =>
                       id.Contains("haiku", StringComparison.OrdinalIgnoreCase))
                   ?? models.FirstOrDefault(id =>
                       id.Contains("sonnet", StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            throw new InvalidOperationException(
                "Anthropic API svarte, men ingen rimelig Haiku- eller Sonnet-modell er tilgjengelig.");
        }

        return model;
    }

    public static async Task<ClaudeResult> Run(
        Stage stage,
        string model,
        string prompt,
        string appendSystemPrompt,
        string workingDir,
        bool loadProjectMcp = true)
    {
        var (fileName, prefix) = ResolveCommand();
        var verbose = IsEnabled(Environment.GetEnvironmentVariable("CLAUDE_VERBOSE"));

        List<string> args =
        [
            .. prefix,
            "-p",
            // stream-json writes one JSON line per message. When a stage is stopped
            // by the time limit there is no final result, but the usage of every
            // message so far is still in the output and can be priced.
            "--output-format", "stream-json",
            "--verbose",
            "--model", model,
            "--max-turns", stage.MaxTurns.ToString(),
            "--permission-mode", stage.CanWrite ? "acceptEdits" : "dontAsk",
        ];

        if (verbose)
        {
            Log.Info($"LLM session logging enabled stage={stage.Name}");
        }

        if (stage.AllowedTools.Count > 0)
        {
            args.Add("--allowedTools");
            args.Add(string.Join(",", stage.AllowedTools));
        }

        // MCP servers come from .mcp.json in the repository. Pass the file
        // explicitly because headless runs otherwise skip project servers that
        // have not been approved interactively. Mention mode runs on a checked-out
        // PR, where .mcp.json could start any command, so it is never loaded there.
        var mcpConfig = Path.Combine(workingDir, ".mcp.json");
        if (loadProjectMcp && File.Exists(mcpConfig))
        {
            args.Add("--mcp-config");
            args.Add(mcpConfig);
        }

        // Pass the system prompt addition through a file and the prompt itself
        // through stdin so length and quoting work on every platform.
        var systemPromptFile = Path.Combine(Path.GetTempPath(), $"mini-nils-system-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(systemPromptFile, appendSystemPrompt);
        args.Add("--append-system-prompt-file");
        args.Add(systemPromptFile);

        var started = DateTimeOffset.Now;
        Log.Info($"Starter Claude Code: stage={stage.Name}, modell={model}, tidsgrense={stage.TimeoutMinutes} min, arbeidsmappe={workingDir}");
        ProcResult result;
        try
        {
            result = await Proc.Run(
                fileName,
                args,
                workingDir,
                stdin: prompt,
                timeout: TimeSpan.FromMinutes(stage.TimeoutMinutes),
                echoStdErr: verbose);
        }
        finally
        {
            File.Delete(systemPromptFile);
        }

        Log.Info($"Claude Code avsluttet: stage={stage.Name}, exitCode={result.ExitCode}");
        if (!result.Ok && string.IsNullOrWhiteSpace(result.StdOut))
        {
            throw new InvalidOperationException(
                $"claude avsluttet med kode {result.ExitCode} uten resultat:\n{result.Tail()}");
        }

        var parsed = Parse(result.StdOut, DateTimeOffset.Now - started, model);
        if (parsed.Text.Contains("not scoped to a workspace", StringComparison.OrdinalIgnoreCase))
        {
            Log.Error(
                "Anthropic-nøkkelen er ikke bundet til et workspace. " +
                "Workeren skal sette anthropic-workspace-id automatisk. " +
                "Bruk nyeste starter-kit, eller sett ANTHROPIC_CUSTOM_HEADERS=" +
                "anthropic-workspace-id: wrkspc_... i .env.");
        }

        return parsed;
    }

    private static bool IsEnabled(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("on", StringComparison.OrdinalIgnoreCase));

    private readonly record struct TokenUsage(long Input, long Output, long CacheRead, long CacheWrite)
    {
        public long Total => Input + Output + CacheRead + CacheWrite;

        public static TokenUsage From(JsonElement usage)
        {
            long Tokens(string name) =>
                usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty(name, out var v) &&
                v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

            return new TokenUsage(
                Tokens("input_tokens"),
                Tokens("output_tokens"),
                Tokens("cache_read_input_tokens"),
                Tokens("cache_creation_input_tokens"));
        }
    }

    private static ClaudeResult Parse(string output, TimeSpan fallbackDuration, string requestedModel)
    {
        // With --output-format stream-json, stdout has one JSON object per line.
        // The last one has type "result" and the totals. Claude Code can repeat an
        // assistant message once per content block, so usage is kept per message id.
        JsonElement? final = null;
        var messages = new Dictionary<string, (string Model, TokenUsage Usage)>();

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('{'))
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(trimmed);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "result")
                {
                    final = root.Clone();
                }
                else if (type == "assistant" &&
                         root.TryGetProperty("message", out var message) &&
                         message.TryGetProperty("usage", out var usage))
                {
                    var id = message.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                    var model = message.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
                    if (model != "<synthetic>")
                    {
                        messages[id.Length > 0 ? id : Guid.NewGuid().ToString()] = (model, TokenUsage.From(usage));
                    }
                }
            }
        }

        if (final is not { } result)
        {
            if (messages.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Fant ingen JSON i svaret fra claude:\n{output[..Math.Min(output.Length, 500)]}");
            }

            // Stopped before the final result, usually by the stage time limit.
            // The tokens were still used and billed, so price them from the messages.
            var sum = Sum(messages.Values.Select(v => v.Usage));
            var estimate = Estimate(messages.Values, requestedModel);
            Log.Error(string.Create(CultureInfo.InvariantCulture, $"Claude Code ble stoppet før sluttresultatet. Kostnaden er beregnet fra {messages.Count} modellsvar: ${estimate:0.0000}."));
            return new ClaudeResult(
                Ok: false,
                Text: "Claude Code ble stoppet før den var ferdig, trolig av tidsgrensen for steget.",
                SessionId: "",
                Turns: messages.Count,
                InputTokens: sum.Input,
                OutputTokens: sum.Output,
                CacheReadTokens: sum.CacheRead,
                CacheCreationTokens: sum.CacheWrite,
                CostUsd: estimate,
                Duration: fallbackDuration,
                CostEstimated: true);
        }

        var totals = TokenUsage.From(result.TryGetProperty("usage", out var u) ? u : default);
        var isError = result.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;

        // Even when the agent reaches the turn limit, it may have done work
        // that should be verified. Build and test decide, not the count.
        var subtype = result.TryGetProperty("subtype", out var st) ? st.GetString() : null;
        if (isError && subtype == "error_max_turns")
        {
            Log.Error("Agenten traff turgrensen. Verifiserer det den rakk.");
            isError = false;
        }
        var durationMs = result.TryGetProperty("duration_ms", out var d) ? d.GetInt64() : (long)fallbackDuration.TotalMilliseconds;

        var cost = result.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : 0m;
        var estimated = false;
        if (cost == 0m && totals.Total > 0)
        {
            // Claude Code has no price for this model. Do not report it as free.
            cost = Estimate(messages.Values, requestedModel, totals);
            estimated = true;
            Log.Error(string.Create(CultureInfo.InvariantCulture, $"Claude Code oppga ingen kostnad for {requestedModel}. Beregnet fra tokens: ${cost:0.0000}."));
        }

        return new ClaudeResult(
            Ok: !isError,
            Text: result.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "",
            SessionId: result.TryGetProperty("session_id", out var s) ? s.GetString() ?? "" : "",
            Turns: result.TryGetProperty("num_turns", out var n) ? n.GetInt32() : 0,
            InputTokens: totals.Input,
            OutputTokens: totals.Output,
            CacheReadTokens: totals.CacheRead,
            CacheCreationTokens: totals.CacheWrite,
            CostUsd: cost,
            Duration: TimeSpan.FromMilliseconds(durationMs),
            CostEstimated: estimated);
    }

    private static TokenUsage Sum(IEnumerable<TokenUsage> usages) =>
        usages.Aggregate(new TokenUsage(0, 0, 0, 0), (a, b) =>
            new TokenUsage(a.Input + b.Input, a.Output + b.Output, a.CacheRead + b.CacheRead, a.CacheWrite + b.CacheWrite));

    /// <summary>
    /// Prices token usage with <see cref="Prices"/>. Uses each message's own model
    /// when known, otherwise the stage model. When only totals are known, the
    /// totals are priced with the stage model.
    /// </summary>
    private static decimal Estimate(
        IEnumerable<(string Model, TokenUsage Usage)> messages,
        string requestedModel,
        TokenUsage? totalsOnly = null)
    {
        var list = messages.ToList();
        if (list.Count == 0 && totalsOnly is { } totals)
        {
            list.Add((requestedModel, totals));
        }

        decimal cost = 0m;
        foreach (var (model, usage) in list)
        {
            var price = PriceFor(model) ?? PriceFor(requestedModel);
            if (price is not { } p)
            {
                Log.Error($"Mangler pris for modellen «{model}». Kostnaden for denne delen er ikke med.");
                continue;
            }

            cost += (usage.Input * p.Input + usage.Output * p.Output +
                     usage.CacheRead * p.CacheRead + usage.CacheWrite * p.CacheWrite) / 1_000_000m;
        }

        return Math.Round(cost, 6);
    }

    private static (decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite)? PriceFor(string model)
    {
        // API responses can carry a dated id, for example claude-haiku-4-5-20251001.
        foreach (var p in Prices)
        {
            if (model.StartsWith(p.Model, StringComparison.OrdinalIgnoreCase))
            {
                return (p.Input, p.Output, p.CacheRead, p.CacheWrite);
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the claude command. In the container and on Linux/macOS it is simply
    /// "claude". On Windows, run the node script directly because the npm shim
    /// (claude.cmd) cannot start without a shell, and a shell breaks multiline arguments.
    /// </summary>
    private static (string FileName, string[] Prefix) ResolveCommand()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CMD");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return (configured, []);
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("claude", []);
        }

        var localBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(localBin))
        {
            return (localBin, []);
        }

        var npmPackage = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm", "node_modules", "@anthropic-ai", "claude-code");
        var npmExe = Path.Combine(npmPackage, "bin", "claude.exe");
        if (File.Exists(npmExe))
        {
            return (npmExe, []);
        }

        var npmCli = Path.Combine(npmPackage, "cli.js");
        if (File.Exists(npmCli))
        {
            return ("node", [npmCli]);
        }

        throw new InvalidOperationException(
            "Fant ikke Claude Code. Installer den (npm install -g @anthropic-ai/claude-code) eller sett CLAUDE_CMD til full sti.");
    }
}
