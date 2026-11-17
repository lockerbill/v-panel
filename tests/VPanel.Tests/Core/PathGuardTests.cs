using VPanel.Core.Utilities;

namespace VPanel.Tests.Core;

public sealed class PathGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vpanel-pg-").FullName;

    [Fact]
    public void Accepts_paths_inside_root()
    {
        Assert.Equal(Path.Combine(_root, "app"), PathGuard.ResolveInside(_root, Path.Combine(_root, "app")));
        Assert.Equal(Path.Combine(_root, "a", "b"), PathGuard.ResolveInside(_root, "a/b"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../etc")]
    [InlineData("a/../../x")]
    [InlineData("/etc/passwd")]
    public void Rejects_escapes(string path) => Assert.Null(PathGuard.ResolveInside(_root, path));

    [Fact]
    public void Rejects_root_itself() => Assert.Null(PathGuard.ResolveInside(_root, _root));

    [Fact]
    public void Rejects_symlink_pointing_outside()
    {
        var outside = Directory.CreateTempSubdirectory("vpanel-out-").FullName;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside);
            Assert.Null(PathGuard.ResolveInside(_root, "link/app"));
        }
        finally { Directory.Delete(outside, true); }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
