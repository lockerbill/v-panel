using VPanel.Core.Abstractions;

namespace VPanel.Tests.Engine;

/// <summary>Records invocations; responses are chosen by the first matching rule.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(Func<IReadOnlyList<string>, bool> Match, Func<ProcessSpec, ProcessResult> Respond)> _rules = [];
    public List<ProcessSpec> Calls { get; } = [];

    public FakeProcessRunner On(Func<IReadOnlyList<string>, bool> match, ProcessResult result) => On(match, _ => result);

    public FakeProcessRunner On(Func<IReadOnlyList<string>, bool> match, Func<ProcessSpec, ProcessResult> respond)
    {
        _rules.Add((match, respond));
        return this;
    }

    public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        Calls.Add(spec);
        var rule = _rules.FirstOrDefault(r => r.Match(spec.Arguments));
        var result = rule.Respond?.Invoke(spec) ?? new ProcessResult(0, "", "");
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)) onOutput?.Invoke(line);
        return Task.FromResult(result);
    }

    /// <summary>Compose sub-command (e.g. "up") of each compose call, in order.</summary>
    public IEnumerable<string> ComposeCommands => Calls
        .Where(c => c.Arguments.FirstOrDefault() == "compose")
        .Select(c => c.Arguments[c.Arguments.ToList().FindIndex(a => a == "--env-file") + 2]);

    public static bool IsCompose(IReadOnlyList<string> a, string sub) =>
        a.FirstOrDefault() == "compose" && a.Contains(sub);
}
