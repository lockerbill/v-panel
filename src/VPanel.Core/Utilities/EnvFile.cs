using System.Text;
using System.Text.RegularExpressions;

namespace VPanel.Core.Utilities;

/// <summary>
/// Minimal .env document model that preserves comments and ordering, supports merging overrides
/// and prepending managed (system) variables.
/// </summary>
public sealed partial class EnvFile
{
    public const string ManagedHeader = "# ---- managed by v-panel (do not edit) ----";
    public const string ManagedFooter = "# ---- end managed by v-panel ----";

    private readonly List<Line> _lines = [];

    private sealed class Line
    {
        public string? Key;
        public string Raw = "";
    }

    [GeneratedRegex(@"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_.\-]*)\s*=(.*)$")]
    private static partial Regex AssignmentRegex();

    public static EnvFile Parse(string? content)
    {
        var file = new EnvFile();
        if (string.IsNullOrEmpty(content)) return file;
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var m = AssignmentRegex().Match(raw);
            file._lines.Add(new Line { Key = m.Success ? m.Groups[1].Value : null, Raw = raw });
        }
        // drop single trailing empty line produced by a final newline
        if (file._lines.Count > 0 && file._lines[^1] is { Key: null, Raw: "" }) file._lines.RemoveAt(file._lines.Count - 1);
        return file;
    }

    public IReadOnlyList<string> Keys => _lines.Where(l => l.Key is not null).Select(l => l.Key!).Distinct().ToList();

    public string? Get(string key)
    {
        var line = _lines.LastOrDefault(l => l.Key == key);
        if (line is null) return null;
        var m = AssignmentRegex().Match(line.Raw);
        return Unquote(m.Groups[2].Value.Trim());
    }

    public IReadOnlyDictionary<string, string> ToDictionary()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var k in Keys) d[k] = Get(k) ?? "";
        return d;
    }

    /// <summary>Sets a key: replaces all existing assignments in place or appends.</summary>
    public void Set(string key, string value)
    {
        var raw = $"{key}={Quote(value)}";
        var existing = _lines.Where(l => l.Key == key).ToList();
        if (existing.Count == 0)
        {
            _lines.Add(new Line { Key = key, Raw = raw });
            return;
        }
        existing[0].Raw = raw;
        foreach (var dup in existing.Skip(1)) _lines.Remove(dup);
    }

    public void Remove(string key) => _lines.RemoveAll(l => l.Key == key);

    /// <summary>Applies another env file on top: overriding keys keep their position, new ones are appended with their comments.</summary>
    public EnvFile MergeWith(EnvFile overrides)
    {
        var result = Clone();
        var pendingComments = new List<Line>();
        foreach (var line in overrides._lines)
        {
            if (line.Key is null)
            {
                pendingComments.Add(line);
                continue;
            }
            var existing = result._lines.Where(l => l.Key == line.Key).ToList();
            if (existing.Count > 0)
            {
                existing[0].Raw = line.Raw;
                foreach (var dup in existing.Skip(1)) result._lines.Remove(dup);
                pendingComments.Clear();
            }
            else
            {
                if (pendingComments.Count > 0 && result._lines.Count > 0 && result._lines[^1].Raw != "" && pendingComments[0].Raw != "")
                    result._lines.Add(new Line { Raw = "" });
                result._lines.AddRange(pendingComments.Select(c => new Line { Raw = c.Raw }));
                pendingComments.Clear();
                result._lines.Add(new Line { Key = line.Key, Raw = line.Raw });
            }
        }
        return result;
    }

    /// <summary>
    /// Renders the file with a managed block on top. Managed variables come first so user values can
    /// reference them (e.g. <c>DB_NAME=app_${VPANEL_INSTANCE}</c>); user assignments of managed keys are dropped.
    /// </summary>
    public string Render(IReadOnlyDictionary<string, string>? managed = null)
    {
        var sb = new StringBuilder();
        if (managed is { Count: > 0 })
        {
            sb.Append(ManagedHeader).Append('\n');
            foreach (var (k, v) in managed) sb.Append(k).Append('=').Append(Quote(v)).Append('\n');
            sb.Append(ManagedFooter).Append('\n');
        }
        foreach (var line in _lines)
        {
            if (line.Key is not null && managed is not null && managed.ContainsKey(line.Key)) continue;
            sb.Append(line.Raw).Append('\n');
        }
        return sb.ToString();
    }

    public override string ToString() => Render();

    private EnvFile Clone()
    {
        var f = new EnvFile();
        f._lines.AddRange(_lines.Select(l => new Line { Key = l.Key, Raw = l.Raw }));
        return f;
    }

    private static string Unquote(string v)
    {
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\''))) return v[1..^1];
        var hash = v.IndexOf(" #", StringComparison.Ordinal);
        return hash >= 0 ? v[..hash].TrimEnd() : v;
    }

    private static string Quote(string v)
    {
        if (v.Length == 0) return "";
        var needs = v.Any(c => char.IsWhiteSpace(c) || c is '#' or '"' or '\'' or '\\');
        if (!needs) return v;
        if (!v.Contains('\'')) return $"'{v}'";
        return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
