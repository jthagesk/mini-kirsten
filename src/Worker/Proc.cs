using System.Diagnostics;
using System.Text;

namespace Worker;

internal sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    public string Tail(int lines = 40)
    {
        var all = (StdOut + "\n" + StdErr).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', all.TakeLast(lines));
    }
}

/// <summary>
/// Runs an external process and collects stdout/stderr. Arguments are passed as
/// a list, so shell quoting is not a concern.
/// </summary>
internal static class Proc
{
    public static async Task<ProcResult> Run(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDir = null,
        string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        bool echo = false,
        bool echoStdErr = false,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            if (echo) Log.Info($"  | {e.Data}");
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            if (echo || echoStdErr) Log.Info($"  ! {e.Data}");
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t)
        {
            cts.CancelAfter(t);
        }

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            return new ProcResult(-1, stdout.ToString(), stderr + $"\n[avbrutt etter {timeout?.TotalMinutes:0} min]");
        }

        return new ProcResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static async Task<ProcResult> Must(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDir = null,
        string? stdin = null,
        TimeSpan? timeout = null,
        bool echo = false)
    {
        var result = await Run(fileName, args, workingDir, stdin, timeout: timeout, echo: echo);
        if (!result.Ok)
        {
            throw new InvalidOperationException(
                $"'{fileName} {string.Join(' ', args.Take(4))}' feilet med kode {result.ExitCode}:\n{result.Tail()}");
        }

        return result;
    }
}

internal static class Log
{
    public static void Info(string message) =>
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {message}");

    public static void Step(string message) =>
        Console.WriteLine($"\n{DateTimeOffset.Now:HH:mm:ss} ── {message}");

    public static void Error(string message) =>
        Console.Error.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} FEIL {message}");
}
