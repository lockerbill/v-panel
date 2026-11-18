using VPanel.Infrastructure.Seed;
using VPanel.Infrastructure.Services;
using VPanel.Web.Cli;

namespace VPanel.Tests.Services;

public class UserServiceTests
{
    [Fact]
    public async Task Create_list_and_reject_duplicates()
    {
        await using var host = await ServiceTestHost.CreateAsync();
        var users = host.Get<UserService>();
        Assert.False(await users.AnyUsersAsync());

        Assert.NotNull((await users.CreateAsync("bob", "password-1")).User);
        Assert.NotNull((await users.CreateAsync("alice", "password-2")).User);
        var (dup, error) = await users.CreateAsync(" bob ", "password-3");
        Assert.Null(dup);
        Assert.Equal("Username already exists.", error);

        Assert.Equal(["alice", "bob"], (await users.ListAsync()).Select(u => u.Username));
    }

    [Fact]
    public async Task Set_password_replaces_the_old_one_and_lifts_lockout()
    {
        await using var host = await ServiceTestHost.CreateAsync();
        var users = host.Get<UserService>();
        await users.CreateAsync("reset-me", "old-password");
        for (var i = 0; i < 5; i++) await users.VerifyAsync("reset-me", "wrong", "1.2.3.4");
        Assert.Equal(LoginOutcome.LockedOut, (await users.VerifyAsync("reset-me", "old-password", "1.2.3.4")).Outcome);

        Assert.NotNull(await users.SetPasswordAsync("reset-me", "short"));
        Assert.NotNull(await users.SetPasswordAsync("nobody", "new-password"));
        Assert.Null(await users.SetPasswordAsync("reset-me", "new-password"));

        Assert.Equal(LoginOutcome.Success, (await users.VerifyAsync("reset-me", "new-password", "1.2.3.4")).Outcome);
        Assert.Equal(LoginOutcome.InvalidCredentials, (await users.VerifyAsync("reset-me", "old-password", "5.6.7.8")).Outcome);
    }

    [Fact]
    public async Task Delete_protects_self_and_last_user()
    {
        await using var host = await ServiceTestHost.CreateAsync();
        var users = host.Get<UserService>();
        var admin = (await users.CreateAsync("admin", "password-1")).User!;
        var other = (await users.CreateAsync("other", "password-2")).User!;

        Assert.Equal("You cannot delete your own account.", (await users.DeleteAsync(admin.Id, "admin")).Error);
        Assert.Equal("other", (await users.DeleteAsync(other.Id, "admin")).Deleted?.Username);
        Assert.False(await users.ExistsAsync(other.Id));
        Assert.True(await users.ExistsAsync(admin.Id));
        Assert.Equal("Cannot delete the last user.", (await users.DeleteAsync(admin.Id, "someone-else")).Error);
    }
}

public class AdminCommandsTests
{
    private sealed class FakeConsole(string input, bool interactive = false) : CliConsole
    {
        private readonly StringReader _in = new(input);
        public StringWriter OutText { get; } = new();
        public StringWriter ErrText { get; } = new();
        public override TextWriter Out => OutText;
        public override TextWriter Error => ErrText;
        public override bool Interactive => interactive;
        public override string? ReadLine() => _in.ReadLine();
        public override string? ReadSecret(string prompt) => _in.ReadLine();
    }

    [Theory]
    [InlineData(new[] { "create-admin" }, "create-admin", null, false, false)]
    [InlineData(new[] { "create-admin", "-u", "alice", "--password-stdin" }, "create-admin", "alice", true, false)]
    [InlineData(new[] { "reset-password", "--username", "bob" }, "reset-password", "bob", false, false)]
    [InlineData(new[] { "--help" }, "help", null, false, false)]
    [InlineData(new[] { "create-admin", "--username" }, "create-admin", null, false, true)]
    [InlineData(new[] { "create-admin", "extra" }, "create-admin", null, false, true)]
    public void Parse(string[] args, string command, string? username, bool stdin, bool hasError)
    {
        Assert.True(AdminCommands.IsCommand(args));
        var inv = AdminCommands.Parse(args);
        Assert.Equal(command, inv.Command);
        Assert.Equal(username, inv.Username);
        Assert.Equal(stdin, inv.PasswordStdin);
        Assert.Equal(hasError, inv.Error is not null);
    }

    [Fact]
    public void Web_host_arguments_are_not_commands()
    {
        Assert.False(AdminCommands.IsCommand([]));
        Assert.False(AdminCommands.IsCommand(["--urls", "http://0.0.0.0:8080"]));
    }

    [Fact]
    public async Task Create_admin_then_reset_password()
    {
        await using var host = await ServiceTestHost.CreateAsync();
        var users = host.Get<UserService>();

        var io = new FakeConsole("password-1\n");
        Assert.Equal(0, await AdminCommands.RunAsync(host.Services, AdminCommands.Parse(["create-admin", "--password-stdin"]), io));
        Assert.Equal(LoginOutcome.Success, (await users.VerifyAsync(AdminCommands.DefaultUsername, "password-1", null)).Outcome);

        io = new FakeConsole("password-2\n");
        Assert.Equal(1, await AdminCommands.RunAsync(host.Services, AdminCommands.Parse(["create-admin", "--password-stdin"]), io));
        Assert.Contains("reset-password", io.ErrText.ToString());

        io = new FakeConsole("password-3\npassword-3\n", interactive: true);
        Assert.Equal(0, await AdminCommands.RunAsync(host.Services, AdminCommands.Parse(["reset-password", "-u", "admin"]), io));
        Assert.Equal(LoginOutcome.Success, (await users.VerifyAsync("admin", "password-3", null)).Outcome);
    }

    [Fact]
    public async Task Create_admin_without_terminal_generates_a_password()
    {
        await using var host = await ServiceTestHost.CreateAsync();
        var io = new FakeConsole("");
        Assert.Equal(0, await AdminCommands.RunAsync(host.Services, AdminCommands.Parse(["create-admin", "-u", "ops"]), io));
        var password = io.OutText.ToString().Split("Generated password (shown only once): ")[1].Trim();
        Assert.Equal(20, password.Length);
        Assert.Equal(LoginOutcome.Success, (await host.Get<UserService>().VerifyAsync("ops", password, null)).Outcome);
    }
}
