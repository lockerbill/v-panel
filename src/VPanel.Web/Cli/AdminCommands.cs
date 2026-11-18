using System.Security.Cryptography;
using System.Text;
using VPanel.Infrastructure.Services;

namespace VPanel.Web.Cli;

/// <summary>
/// Maintenance commands run from the server shell instead of starting the web host, e.g.
/// <c>docker exec -it vpanel vpanel create-admin</c> or <c>sudo -u vpanel /opt/vpanel/app/VPanel.Web create-admin</c>.
/// They use the same configuration and database as the running panel.
/// </summary>
public static class AdminCommands
{
    public const string DefaultUsername = "admin";
    private static readonly string[] HelpNames = ["help", "--help", "-h"];
    private static readonly string[] CommandNames = ["create-admin", "reset-password", .. HelpNames];

    public sealed record Invocation(string Command, string? Username, bool PasswordStdin, string? Error);

    /// <summary>True when the command can run without the database (help, argument errors).</summary>
    public static bool IsStandalone(Invocation inv) => inv.Command == "help" || inv.Error is not null;

    public static bool IsCommand(string[] args) => args.Length > 0 && CommandNames.Contains(args[0]);

    public static Invocation Parse(string[] args)
    {
        var command = HelpNames.Contains(args[0]) ? "help" : args[0];
        string? username = null;
        var stdin = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--username" or "-u" when i + 1 < args.Length:
                    username = args[++i];
                    break;
                case "--password-stdin":
                    stdin = true;
                    break;
                case "--help" or "-h":
                    command = "help";
                    break;
                default:
                    return new Invocation(command, username, stdin, $"Unknown or incomplete option '{args[i]}'.");
            }
        }
        return new Invocation(command, username, stdin, null);
    }

    public static string Usage => """
        Usage: vpanel <command> [options]

        Commands:
          create-admin     Create an administrator account.
          reset-password   Set a new password for an existing account.
          help             Show this help.

        Options:
          -u, --username <name>   Account name (create-admin defaults to "admin").
          --password-stdin        Read the password from the first line of standard input.

        Without --password-stdin the password is asked for on the terminal (hidden). When no terminal is
        attached, create-admin generates a random password and prints it once.

        Examples:
          docker exec -it vpanel vpanel create-admin
          docker exec -it vpanel vpanel reset-password --username admin
          echo 'S3cret-pass' | docker exec -i vpanel vpanel create-admin -u alice --password-stdin
          sudo -u vpanel /opt/vpanel/app/VPanel.Web create-admin      (systemd install)
        """;

    /// <summary>Runs an invocation that needs no services (<see cref="IsStandalone"/>): usage errors and <c>help</c>.</summary>
    public static int RunStandalone(Invocation inv, CliConsole io)
    {
        if (inv.Error is not null)
        {
            io.Error.WriteLine(inv.Error);
            io.Error.WriteLine("Run 'vpanel help' for usage.");
            return 2;
        }
        if (inv.Command == "help")
        {
            io.Out.WriteLine(Usage);
            return 0;
        }
        throw new InvalidOperationException($"Command '{inv.Command}' needs services; use RunAsync.");
    }

    public static async Task<int> RunAsync(IServiceProvider services, Invocation inv, CliConsole io)
    {
        if (IsStandalone(inv)) return RunStandalone(inv, io);
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
        switch (inv.Command)
        {
            case "create-admin":
                return await CreateAdminAsync(users, audit, inv, io);
            case "reset-password":
                return await ResetPasswordAsync(users, audit, inv, io);
            default:
                throw new InvalidOperationException($"Unhandled command '{inv.Command}'.");
        }
    }

    private static async Task<int> CreateAdminAsync(UserService users, AuditService audit, Invocation inv, CliConsole io)
    {
        var username = inv.Username;
        if (username is null && io.Interactive && !inv.PasswordStdin)
        {
            io.Out.Write($"Username [{DefaultUsername}]: ");
            username = io.ReadLine();
        }
        username = string.IsNullOrWhiteSpace(username) ? DefaultUsername : username.Trim();

        var generated = false;
        string? password;
        if (inv.PasswordStdin) password = io.ReadLine();
        else if (io.Interactive) password = PromptNewPassword(io);
        else
        {
            password = GeneratePassword();
            generated = true;
        }
        if (password is null) return 1;

        var (user, error) = await users.CreateAsync(username, password);
        if (user is null)
        {
            io.Error.WriteLine($"Error: {error}");
            if (error == "Username already exists.")
                io.Error.WriteLine($"To change its password run: vpanel reset-password --username {username}");
            return 1;
        }
        await audit.LogAsync("cli", "auth.create_admin_cli", target: user.Username);
        io.Out.WriteLine($"Admin account '{user.Username}' created.");
        if (generated) io.Out.WriteLine($"Generated password (shown only once): {password}");
        return 0;
    }

    private static async Task<int> ResetPasswordAsync(UserService users, AuditService audit, Invocation inv, CliConsole io)
    {
        if (string.IsNullOrWhiteSpace(inv.Username))
        {
            io.Error.WriteLine("Error: --username is required.");
            return 2;
        }
        string? password;
        if (inv.PasswordStdin) password = io.ReadLine();
        else if (io.Interactive) password = PromptNewPassword(io);
        else
        {
            io.Error.WriteLine("Error: no terminal attached. Use 'docker exec -it ...' or pass the password with --password-stdin.");
            return 1;
        }
        if (password is null) return 1;

        var error = await users.SetPasswordAsync(inv.Username, password);
        if (error is not null)
        {
            io.Error.WriteLine($"Error: {error}");
            return 1;
        }
        await audit.LogAsync("cli", "auth.reset_password_cli", target: inv.Username.Trim());
        io.Out.WriteLine($"Password for '{inv.Username.Trim()}' updated.");
        return 0;
    }

    private static string? PromptNewPassword(CliConsole io)
    {
        var password = io.ReadSecret("Password: ");
        if (password is null) return null;
        if (password.Length < UserService.MinPasswordLength)
        {
            io.Error.WriteLine($"Error: password must be at least {UserService.MinPasswordLength} characters.");
            return null;
        }
        if (io.ReadSecret("Confirm password: ") != password)
        {
            io.Error.WriteLine("Error: passwords do not match.");
            return null;
        }
        return password;
    }

    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alphabet, 20);
    }
}

/// <summary>Console abstraction so the commands can be tested.</summary>
public class CliConsole
{
    public virtual TextWriter Out => Console.Out;
    public virtual TextWriter Error => Console.Error;
    public virtual bool Interactive => !Console.IsInputRedirected;
    public virtual string? ReadLine() => Console.In.ReadLine();

    public virtual string? ReadSecret(string prompt)
    {
        Out.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Out.WriteLine();
        return sb.ToString();
    }
}
