namespace VPanel.Core.Utilities;

public static class PathGuard
{
    /// <summary>
    /// Returns the normalized absolute path if <paramref name="path"/> lies strictly inside
    /// <paramref name="root"/> (after resolving "..", and symlinks of existing segments); otherwise null.
    /// Relative paths are resolved against the root.
    /// </summary>
    public static string? ResolveInside(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return null;
        if (path.Contains('\0')) return null;
        var fullRoot = Normalize(Path.GetFullPath(root));
        var full = Normalize(Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(fullRoot, path)));
        if (!IsStrictlyInside(fullRoot, full)) return null;

        // Resolve symlinks of the longest existing prefix to block escapes via links.
        var realRoot = Normalize(RealPath(fullRoot));
        var realFull = Normalize(RealPath(full));
        if (!IsStrictlyInside(realRoot, realFull)) return null;
        return full;
    }

    public static bool IsStrictlyInside(string root, string path)
    {
        var r = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(r, StringComparison.Ordinal) && path.Length > r.Length;
    }

    private static string Normalize(string p) => p.Length > 1 ? p.TrimEnd(Path.DirectorySeparatorChar) : p;

    /// <summary>Resolves symlinks for the existing part of the path, appending the non-existing remainder.</summary>
    private static string RealPath(string path)
    {
        var existing = path;
        var remainder = new Stack<string>();
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null) return path;
            remainder.Push(Path.GetFileName(existing));
            existing = parent;
        }
        var resolved = ResolveLinks(existing);
        foreach (var part in remainder) resolved = Path.Combine(resolved, part);
        return resolved;
    }

    private static string ResolveLinks(string existingPath)
    {
        var parts = existingPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = Path.GetPathRoot(existingPath) ?? "/";
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null) current = target.FullName;
            }
        }
        return current;
    }
}
