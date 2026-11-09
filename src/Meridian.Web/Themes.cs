using MudBlazor;

namespace Meridian.Web;

public static class Themes
{
    public static readonly MudTheme Meridian = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#4f8cff", Secondary = "#2dd4bf", Tertiary = "#a283ff",
            Background = "#e9edf4", Surface = "#ffffff", DrawerBackground = "#ffffff", AppbarBackground = "#ffffff", AppbarText = "#141b2b",
            TextPrimary = "#141b2b", TextSecondary = "#4c586d", TextDisabled = "#8a94a7",
            LinesDefault = "rgba(15,23,42,.10)", LinesInputs = "rgba(15,23,42,.18)", TableLines = "rgba(15,23,42,.08)", Divider = "rgba(15,23,42,.08)",
            ActionDefault = "#4c586d", Error = "#e5484d", Warning = "#d39e00", Success = "#2e9e6a", Info = "#4f8cff",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#4f8cff", Secondary = "#2dd4bf", Tertiary = "#a283ff",
            Background = "#0a0f19", BackgroundGray = "#131b2b", Surface = "#131b2b", DrawerBackground = "#131b2b", AppbarBackground = "#0d1320", AppbarText = "#e7ecf6",
            TextPrimary = "#e7ecf6", TextSecondary = "#a3aec4", TextDisabled = "#6d7a93",
            LinesDefault = "rgba(148,163,184,.14)", LinesInputs = "rgba(148,163,184,.26)", TableLines = "rgba(148,163,184,.10)", Divider = "rgba(148,163,184,.12)",
            ActionDefault = "#a3aec4", ActionDisabled = "#4d5970", Error = "#ff6b6b", Warning = "#f0b429", Success = "#3fbf7f", Info = "#4f8cff",
            OverlayDark = "rgba(5,8,14,.6)",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["Inter", "-apple-system", "Segoe UI", "Helvetica", "Arial", "sans-serif"], FontSize = "0.8125rem", LetterSpacing = "-0.003em" },
            Button = new ButtonTypography { FontFamily = ["Inter", "sans-serif"], TextTransform = "none", FontWeight = "500" },
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px", AppbarHeight = "48px" },
    };
}
