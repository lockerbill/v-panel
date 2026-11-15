using MudBlazor;

namespace VPanel.Web.Components.Layout;

public static class VPanelTheme
{
    private static readonly string[] Sans = ["Inter", "ui-sans-serif", "system-ui", "-apple-system", "Segoe UI", "Roboto", "Helvetica Neue", "Arial", "sans-serif"];

    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#0f766e",
            Secondary = "#475569",
            Tertiary = "#7c3aed",
            Info = "#0369a1",
            Success = "#15803d",
            Warning = "#b45309",
            Error = "#b91c1c",
            AppbarBackground = "#ffffff",
            AppbarText = "#0f172a",
            Background = "#f6f7f9",
            Surface = "#ffffff",
            DrawerBackground = "#ffffff",
            DrawerText = "#334155",
            DrawerIcon = "#64748b",
            TextPrimary = "#0f172a",
            TextSecondary = "#475569",
            LinesDefault = "#e2e8f0",
            TableLines = "#e2e8f0",
            Divider = "#e2e8f0",
            ActionDefault = "#475569",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#2dd4bf",
            Secondary = "#94a3b8",
            Tertiary = "#a78bfa",
            Info = "#38bdf8",
            Success = "#4ade80",
            Warning = "#fbbf24",
            Error = "#f87171",
            AppbarBackground = "#0f141a",
            AppbarText = "#e2e8f0",
            Background = "#0b0f14",
            Surface = "#121821",
            DrawerBackground = "#0f141a",
            DrawerText = "#cbd5e1",
            DrawerIcon = "#94a3b8",
            TextPrimary = "#e2e8f0",
            TextSecondary = "#94a3b8",
            LinesDefault = "#1f2a37",
            TableLines = "#1f2a37",
            Divider = "#1f2a37",
            ActionDefault = "#94a3b8",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = Sans },
            H4 = new H4Typography { FontFamily = Sans, FontWeight = "650", FontSize = "1.6rem", LetterSpacing = "-0.01em" },
            H5 = new H5Typography { FontFamily = Sans, FontWeight = "600", FontSize = "1.25rem" },
            H6 = new H6Typography { FontFamily = Sans, FontWeight = "600", FontSize = "1.05rem" },
            Button = new ButtonTypography { FontFamily = Sans, TextTransform = "none", FontWeight = "550" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px", DrawerWidthLeft = "232px" },
    };
}
