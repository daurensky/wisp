using System.Windows;

namespace Wisp.Desktop;

/// <summary>One logical-DPI banner contract is shared by profile rendering, crop and encoding.</summary>
public static class ProfileLayout
{
    public const int BannerWidth = 400;
    public const int BannerHeight = 140;
    public const double BannerRatio = (double)BannerWidth / BannerHeight;
    public static double Inset => Ui.L;
    public static double ContentWidth => BannerWidth + Inset * 2;
    public static double CardWidth => ContentWidth + Ui.Metric("Size.Hairline") * 2;
    public static Thickness ContentInsets => Ui.Insets(Inset);
    public static Thickness FieldMargin => Ui.Insets(0, Ui.Xs, 0, Ui.M);
    public static Thickness SectionMargin => Ui.Insets(0, Ui.M, 0, Ui.S);
    public static Thickness ActionMargin => Ui.Insets(0, 0, Ui.S, 0);
}
