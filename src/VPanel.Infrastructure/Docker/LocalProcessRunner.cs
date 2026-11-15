using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using VPanel.Core.Abstractions;

namespace VPanel.Infrastructure.Docker;

/// <summary>Runs processes on the local machine without a shell (arguments are passed as a list).</summary>
public sealed class LocalProcessRunner(ILogger<LocalProcessRunner> logger) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in spec.Arguments) psi.ArgumentList.Add(a);
        if (spec.WorkingDirectory is not null) psi.WorkingDirectory = spec.WorkingDirectory;
        psi.Environment["DOCKER_CLI_HINTS"] = "false";
        psi.Environment["NO_COLOR"] = "1";
        if (spec.Environment is not null)
            foreach (var (k, v) in spec.Environment) psi.Environment[k] = v;

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdoutDone = new TaskCompletionSource();
        var stderrDone = new TaskCompletionSource();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { stdoutDone.TrySetResult(); return; }
            lock (stdout) stdout.Append(e.Data).Append('\n');
            SafeInvoke(onOutput, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { stderrDone.TrySetResult(); return; }
            lock (stderr) stderr.Append(e.Data).Append('\n');
            SafeInvoke(onOutput, e.Data);
        };

        logger.LogDebug("exec: {Command}", spec);
        try
        {
            if (!process.Start()) return new ProcessResult(-1, "", $"Failed to start {spec.FileName}");
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", $"Failed to start '{spec.FileName}': {ex.Message}");
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = spec.Timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(stdoutDone.Task, stderrDone.Task).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ContinueWith(_ => { }, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, stdout.ToString(), stderr + $"\nTimed out after {spec.Timeout}");
        }
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private void SafeInvoke(Action<string>? cb, string line)
    {
        if (cb is null) return;
        try { cb(line); } catch (Exception ex) { logger.LogWarning(ex, "Output callback failed"); }
    }
}
