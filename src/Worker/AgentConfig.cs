using System.Text.Json;
using System.Text.Json.Serialization;

namespace Worker;

/// <summary>
/// Contents of agent/stages.json. Participants edit this file in Lab 1 and Lab 3.
/// </summary>
internal sealed class AgentConfig
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "claude-sonnet-5";

    [JsonPropertyName("verify")]
    public List<string> Verify { get; set; } = [];

    [JsonPropertyName("stopOnVerifyFailure")]
    public bool StopOnVerifyFailure { get; set; }

    [JsonPropertyName("stages")]
    public List<Stage> Stages { get; set; } = [];

    public static AgentConfig Load(string agentDir)
    {
        var path = Path.Combine(agentDir, "stages.json");
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AgentConfig>(json, new JsonSerializerOptions
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) ?? throw new InvalidOperationException($"Kunne ikke lese {path}");

        if (config.Stages.Count == 0)
        {
            throw new InvalidOperationException($"{path} må ha minst én stage.");
        }

        // CLAUDE_MODEL overrides the default model so the Azure deploy workflow can set it.
        // Stages with their own "model" keep it.
        if (Environment.GetEnvironmentVariable("CLAUDE_MODEL") is { Length: > 0 } model)
        {
            config.Model = model;
        }

        // Fable is not allowed in the course, not even as a stage override.
        // Stop before anything runs so it cannot be used by accident.
        var forbidden = new[] { config.Model }
            .Concat(config.Stages.Select(s => s.Model))
            .FirstOrDefault(m => m is not null && m.Contains("fable", StringComparison.OrdinalIgnoreCase));
        if (forbidden is not null)
        {
            throw new InvalidOperationException(
                $"Modellen «{forbidden}» er ikke tillatt i kurset. Bruk Sonnet, Opus eller Haiku i {path} og i CLAUDE_MODEL.");
        }

        return config;
    }
}

internal sealed class Stage
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>File name under agent/prompts/.</summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    /// <summary>Overrides the model for this stage. Empty means AgentConfig.Model.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>Pre-approved Claude Code tools, such as "Read", "Edit", or "Bash(dotnet test:*)".</summary>
    [JsonPropertyName("allowedTools")]
    public List<string> AllowedTools { get; set; } = [];

    [JsonPropertyName("maxTurns")]
    public int MaxTurns { get; set; } = 60;

    [JsonPropertyName("timeoutMinutes")]
    public int TimeoutMinutes { get; set; } = 15;

    /// <summary>
    /// Stages without write tools run in read-only mode, where everything else
    /// is rejected. Tool names determine the profile.
    /// </summary>
    public bool CanWrite => AllowedTools.Any(t =>
        t.StartsWith("Edit", StringComparison.Ordinal) ||
        t.StartsWith("Write", StringComparison.Ordinal) ||
        t.StartsWith("MultiEdit", StringComparison.Ordinal) ||
        t.StartsWith("NotebookEdit", StringComparison.Ordinal));
}
