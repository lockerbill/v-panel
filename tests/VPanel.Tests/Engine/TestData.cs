using VPanel.Core.Configuration;
using VPanel.Core.Domain;

namespace VPanel.Tests.Engine;

internal static class TestData
{
    public static PanelSettings Settings(string appsRoot = "/opt/vpanel/apps", string caddyDir = "/opt/vpanel/caddy") =>
        new() { AppsRoot = appsRoot, CaddyDir = caddyDir, SharedNetwork = "vpanel-shared", CaddyContainer = "caddy" };

    public static AppInstance Instance(string slug = "shop-01", string? publicUrl = "https://shop-01.example.com", string appsRoot = "/opt/vpanel/apps")
    {
        var app = new AppDefinition
        {
            Id = 1,
            Name = "Shop",
            Slug = "shop",
            ComposeTemplate = "services:\n  web:\n    image: nginx\n    volumes:\n      - data:/data\nvolumes:\n  data:\n",
            EnvTemplate = "DB_NAME=shop_${VPANEL_INSTANCE}\nGREETING=hello\n",
            DefaultProxyService = "web",
            DefaultProxyPort = 80,
        };
        return new AppInstance
        {
            Id = 1,
            AppId = 1,
            App = app,
            Name = slug,
            Slug = slug,
            PublicUrl = publicUrl,
            InstallPath = Path.Combine(appsRoot, slug),
            EnvOverrides = "GREETING=hi\n",
        };
    }
}
