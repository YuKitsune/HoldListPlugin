using System.Windows;
using System.Windows.Media;

namespace HoldPlugin;

public static class Theme
{
    public static float Alpha = 0.4f;
    public static Brush LightBrush = new SolidColorBrush(Color.FromScRgb(Alpha, 255, 255, 255));
    public static Brush DarkBrush = new SolidColorBrush(Color.FromScRgb(Alpha, 0, 0, 0));

    public static SolidColorBrush BackgroundColor { get; set; } = new(System.Drawing.Color.FromArgb(255, 160, 170, 170).ToWindowsColor());
    public static SolidColorBrush SelectedButtonColor { get; set; } = new(System.Drawing.Color.FromArgb(255, 0, 0, 96).ToWindowsColor());
    public static SolidColorBrush GenericTextColor { get; set; } = new(System.Drawing.Color.FromArgb(255, 96, 0, 0).ToWindowsColor());
    public static SolidColorBrush InteractiveTextColor { get; set; } = new(System.Drawing.Color.FromArgb(255, 0, 0, 96).ToWindowsColor());
    public static SolidColorBrush NonInteractiveTextColor { get; set; } = new(System.Drawing.Color.FromArgb(255, 90, 90, 90).ToWindowsColor());

    public static SolidColorBrush ItemBackgroundColor { get; set; } = new(System.Drawing.Color.FromArgb(255, 130, 146, 146).ToWindowsColor());
    public static SolidColorBrush JurisdictionColor { get; set; } = new(System.Drawing.Color.FromArgb(255, 170, 255, 170).ToWindowsColor());
    public static SolidColorBrush HandoverColor { get; set; } = new(System.Drawing.Color.FromArgb(255, 255, 205, 105).ToWindowsColor());
    public static SolidColorBrush StripTextColor { get; set; } = new(System.Drawing.Color.FromArgb(255, 220, 220, 220).ToWindowsColor());

    // TODO: Support live updating font sizes
    public static FontFamily FontFamily { get; set; } = new("Terminus (TTF)");
    public static double FontSize { get; set; } = 16.0;
    public static FontWeight FontWeight { get; set; } = FontWeights.Bold;

    public static Thickness BeveledBorderThickness = new(2);
    public static double BeveledLineWidth = 4;
}
