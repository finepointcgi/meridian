using MudBlazor;

namespace Meridian.Web;

public static class Themes
{
    /// <summary>The default theme (blue accent). The live theme comes from <see cref="Settings.ThemeService.MudTheme"/>.</summary>
    public static readonly MudTheme Meridian = Create("#2f6fec", "#ffffff", "#4f8cff", "#0a0f19");

    /// <summary>
    /// The Meridian MudBlazor theme with a given primary colour per mode (and the text colour drawn on it). Text tokens
    /// meet WCAG AA (4.5:1) on the surfaces they are used on.
    /// </summary>
    public static MudTheme Create(string lightPrimary, string lightOnPrimary, string darkPrimary, string darkOnPrimary) => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = lightPrimary, PrimaryContrastText = lightOnPrimary, Secondary = "#0f8f80", Tertiary = "#7c5ce0",
            Background = "#e9edf4", BackgroundGray = "#f1f4f9", Surface = "#ffffff", DrawerBackground = "#ffffff", AppbarBackground = "#ffffff", AppbarText = "#141b2b",
            TextPrimary = "#141b2b", TextSecondary = "#4c586d", TextDisabled = "#6b7487",
            LinesDefault = "rgba(15,23,42,.12)", LinesInputs = "rgba(15,23,42,.24)", TableLines = "rgba(15,23,42,.08)", Divider = "rgba(15,23,42,.10)",
            ActionDefault = "#4c586d", Error = "#c9372c", Warning = "#a86a00", Success = "#1f7a52", Info = lightPrimary,
        },
        PaletteDark = new PaletteDark
        {
            Primary = darkPrimary, PrimaryContrastText = darkOnPrimary, Secondary = "#2dd4bf", Tertiary = "#a283ff",
            Background = "#0a0f19", BackgroundGray = "#131b2b", Surface = "#131b2b", DrawerBackground = "#131b2b", AppbarBackground = "#0d1320", AppbarText = "#e7ecf6",
            TextPrimary = "#e7ecf6", TextSecondary = "#a3aec4", TextDisabled = "#7d8aa3",
            LinesDefault = "rgba(148,163,184,.14)", LinesInputs = "rgba(148,163,184,.26)", TableLines = "rgba(148,163,184,.10)", Divider = "rgba(148,163,184,.12)",
            ActionDefault = "#a3aec4", ActionDisabled = "#4d5970", Error = "#ff6b6b", Warning = "#f0b429", Success = "#3fbf7f", Info = darkPrimary,
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
