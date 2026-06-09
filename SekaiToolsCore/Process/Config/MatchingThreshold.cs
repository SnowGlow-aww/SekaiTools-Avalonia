namespace SekaiToolsCore.Process.Config;

public struct MatchingThreshold()
{
    public double DialogNametagNormal { get; init; } = 0.70;
    public double DialogNametagSpecial { get; init; } = 0.70;
    public double DialogContentNormal { get; init; } = 0.70;
    public double DialogContentSpecial { get; init; } = 0.70;
    public double BannerNormal { get; init; } = 0.50;
    public double MarkerNormal { get; init; } = 0.50;

    // How long (seconds) to keep an already-matched dialog alive through transient
    // sub-threshold frames before finalizing its timeline. 0 disables the grace
    // (revert to the old behavior of ending the line on the first failed frame).
    public double DialogDropGraceSeconds { get; init; } = 0.30;
}