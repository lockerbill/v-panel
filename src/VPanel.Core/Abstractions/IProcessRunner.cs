namespace VPanel.Core.Abstractions;

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public TimeSpan? Timeout { get; init; }

    public override string ToString() =>
        FileName + " " + string.Join(' ', Arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
    public string Combined => string.IsNullOrEmpty(StdErr) ? StdOut : StdOut + StdErr;
}

/// <summary>
/// Runs external processes (docker CLI). Abstracted so the engine is testable and can later
/// target remote servers (e.g. an SSH runner).
/// </summary>
public interface IProcessRunner
{
    /// <param name="onOutput">Optional callback for each stdout/stderr line as it is produced.</param>
    Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string>? onOutput = null, CancellationToken ct = default);
}
