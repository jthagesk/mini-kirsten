using System.Text.Json;

namespace Worker;

/// <summary>
/// Checks whether an issue is still open before starting work.
///
/// Multiple participants may work on the same issue. The first merged PR wins;
/// Sensor rejects competing open PRs after that merge.
/// </summary>
internal static class Reservation
{
    /// <summary>Null when the issue is open; otherwise a short reason to leave it.</summary>
    public static async Task<string?> IsBusy(AgentTask task, string agentName)
    {
        Log.Info("Sjekker om issuen er ledig ...");
        var response = await Proc.Must("gh",
            ["issue", "view", task.IssueNumber.ToString(), "--repo", task.Repo, "--json", "state"],
            timeout: TimeSpan.FromSeconds(30));

        using var doc = JsonDocument.Parse(response.StdOut);
        var root = doc.RootElement;
        var state = root.GetProperty("state").GetString();
        Log.Info($"Issue-status: {state}");

        if (string.Equals(state, "CLOSED", StringComparison.OrdinalIgnoreCase))
        {
            return "issuen er lukket";
        }

        Log.Info("Issuen er åpen. Flere agenter kan jobbe parallelt.");
        return null;
    }

    /// <summary>Assigns the issue to the user behind GH_TOKEN for status visibility.</summary>
    public static async Task Claim(AgentTask task)
    {
        Log.Info("Tildeler issue til GitHub-brukeren for statusvisning ...");
        var r = await Proc.Run(
            "gh",
            ["issue", "edit", task.IssueNumber.ToString(), "--repo", task.Repo, "--add-assignee", "@me"],
            timeout: TimeSpan.FromSeconds(30));
        if (r.Ok) Log.Info("Issuen er tildelt meg.");
        else Log.Error($"Klarte ikke å tildele issuen: {r.Tail(3)}");
    }
}
