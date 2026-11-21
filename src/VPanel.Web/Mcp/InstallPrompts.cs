using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VPanel.Web.Mcp;

[McpServerPromptType]
public sealed class InstallPrompts
{
    [McpServerPrompt(Name = "install_app_from_repo")]
    [Description("Install an application from a git repository on this server and have v-panel manage it.")]
    public static string InstallAppFromRepo(
        [Description("Repository URL, e.g. https://github.com/owner/repo")] string repo_url,
        [Description("Public URL to serve the app at, e.g. https://myapp.example.com")] string? public_url = null,
        [Description("Instance name/slug, e.g. myapp-01")] string? instance_name = null) =>
        McpGuide.InstallPrompt(repo_url, public_url, instance_name);
}
