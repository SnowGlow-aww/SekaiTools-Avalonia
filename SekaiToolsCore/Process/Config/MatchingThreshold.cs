namespace SekaiToolsCore.Process.Config;

public struct MatchingThreshold()
{
    // 模板渲染几何已忠实复刻 1.3.3 后，恢复接近旧版(0.85)的阈值；普通项取 0.80 给
    // SkiaSharp 与 GDI+ 抗锯齿差异留一点余量，抖动/特殊项必须低于普通项(旧版 0.6)以便捕获抖动台词。
    public double DialogNametagNormal { get; init; } = 0.80;
    public double DialogNametagSpecial { get; init; } = 0.60;
    public double DialogContentNormal { get; init; } = 0.80;
    public double DialogContentSpecial { get; init; } = 0.60;
    public double BannerNormal { get; init; } = 0.80;

    // 横幅淡入/淡出边界的低阈值：横幅文字以 100~300ms 淡入（首条 300ms），匹配值
    // 从噪声(~0.13-0.19)爬到 0.9+ 只需 2-4 帧；用低阈值把「刚开始显形/尚未消失」
    // 的帧计入起止，修掉正常阈值造成的起始偏晚 4-8 帧 / 结束偏早 1-5 帧
    // （用 event208 成品视频 + 人工校对基准逐帧标定）。
    public double BannerFadeLow { get; init; } = 0.30;
    public double MarkerNormal { get; init; } = 0.80;

    // How long (seconds) to keep an already-matched dialog alive through transient
    // sub-threshold frames before finalizing its timeline. 0 disables the grace
    // (revert to the old behavior of ending the line on the first failed frame).
    public double DialogDropGraceSeconds { get; init; } = 0.30;

    // 卡住跳过(look-ahead)：当前对话连续这么多秒都匹配不到名牌时，开始探测"下一条对话"的
    // 名牌；一旦下一条出现在画面里，就判定当前这条被漏掉(如 MV/演出里某条难匹配的行)、
    // 标记跳过并前进，避免一条卡死整集。0 = 关闭(回到严格按序、卡住就停的旧行为)。
    // 注意：只有"下一条真的出现"才会跳，所以长 MV 间隔里不会误跳后面的对话。
    public double DialogStuckSkipSeconds { get; init; } = 3.0;
}