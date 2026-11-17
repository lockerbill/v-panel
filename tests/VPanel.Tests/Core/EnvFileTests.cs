using VPanel.Core.Utilities;

namespace VPanel.Tests.Core;

public class EnvFileTests
{
    [Fact]
    public void Parse_reads_values_and_strips_quotes()
    {
        var env = EnvFile.Parse("# comment\nA=1\nexport B=\"two words\"\nC='x'\nD=value # trailing\n");
        Assert.Equal("1", env.Get("A"));
        Assert.Equal("two words", env.Get("B"));
        Assert.Equal("x", env.Get("C"));
        Assert.Equal("value", env.Get("D"));
        Assert.Equal(["A", "B", "C", "D"], env.Keys);
    }

    [Fact]
    public void Merge_overrides_in_place_and_appends_new_keys()
    {
        var baseEnv = EnvFile.Parse("# db\nDB_HOST=postgres\nDB_NAME=app\n");
        var merged = baseEnv.MergeWith(EnvFile.Parse("DB_NAME=app_01\n# extra\nFEATURE=on\n"));
        var text = merged.Render();
        Assert.Equal("# db\nDB_HOST=postgres\nDB_NAME=app_01\n\n# extra\nFEATURE=on\n", text);
    }

    [Fact]
    public void Render_puts_managed_block_first_and_drops_user_copies()
    {
        var env = EnvFile.Parse("VPANEL_INSTANCE=hacked\nX=${VPANEL_INSTANCE}\n");
        var text = env.Render(new Dictionary<string, string> { ["VPANEL_INSTANCE"] = "app-01" });
        Assert.StartsWith(EnvFile.ManagedHeader + "\nVPANEL_INSTANCE=app-01\n" + EnvFile.ManagedFooter + "\n", text);
        Assert.DoesNotContain("hacked", text);
        Assert.Contains("X=${VPANEL_INSTANCE}", text);
    }

    [Fact]
    public void Set_quotes_values_with_spaces()
    {
        var env = EnvFile.Parse("");
        env.Set("A", "hello world");
        Assert.Equal("A='hello world'\n", env.Render());
        Assert.Equal("hello world", EnvFile.Parse(env.Render()).Get("A"));
    }
}
