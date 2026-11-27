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

    /// <summary>Adds a rule that takes precedence over the rules registered so far.</summary>
    public FakeProcessRunner Before(Func<IReadOnlyList<string>, bool> match, Func<ProcessSpec, ProcessResult> respond)
    {
        lock (_rules) _rules.Insert(0, (match, respond));
        return this;
    }

    public FakeProcessRunner Before(Func<IReadOnlyList<string>, bool> match, ProcessResult result) => Before(match, _ => result);

    public Task<ProcessResult> RunAsync(ProcessSpec spec, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        (Func<IReadOnlyList<string>, bool> Match, Func<ProcessSpec, ProcessResult> Respond) rule;
        lock (_rules) // background workers (status sync, deployments) may call concurrently in host-level tests
        {
            Calls.Add(spec);
            rule = _rules.FirstOrDefault(r => r.Match(spec.Arguments));
        }
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
