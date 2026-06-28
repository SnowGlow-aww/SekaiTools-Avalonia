using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;

namespace SekaiToolsCore.Match.TemplateMatcher;

public class TemplateMatchCachePool
{
    public enum MatchUsage
    {
        ContentStartSign = 0,
        Banner = 1,
        DialogNameTag = 2,
        DialogContent1 = 3,
        DialogContent2 = 4,
        DialogContent3 = 5,
        Marker = 6,
        Misc = 7
    }

    private static List<TemplateMatchCachePool>? _globalPool;
    public Mat diffMat;

    public Mat? prevImg;
    public Size prevTemplateSize;
    public TemplateMatchResult prevResult;

    public TemplateMatchCachePool()
    {
        diffMat = new Mat();
    }

    private static List<TemplateMatchCachePool> GlobalPool
    {
        get
        {
            if (_globalPool != null) return _globalPool;
            const int len = (int)MatchUsage.Misc + 1;
            _globalPool = new List<TemplateMatchCachePool>(len);

            for (var i = 0; i < len; i++) _globalPool.Add(new TemplateMatchCachePool());

            return _globalPool;
        }
    }

    public static TemplateMatchCachePool GetPool(MatchUsage usage)
    {
        return GlobalPool[(int)usage];
    }

    // Drop the process-wide pool so a new run starts with no carried-over prevImg/
    // prevResult from the previous video. The old pool instances become unrooted and
    // are reclaimed by GC; the next GetPool rebuilds a fresh set. Called at每次开新打轴.
    public static void ResetAll()
    {
        _globalPool = null;
    }

    public static void NextDialog()
    {
        GlobalPool[(int)MatchUsage.DialogNameTag].Reset();
        GlobalPool[(int)MatchUsage.DialogContent1].Reset();
        GlobalPool[(int)MatchUsage.DialogContent2].Reset();
        GlobalPool[(int)MatchUsage.DialogContent3].Reset();
    }

    public void RegisterResult(Mat img, Size templateSize, TemplateMatchResult result)
    {
        prevImg = img;
        prevTemplateSize = templateSize;
        prevResult = result;
    }

    public bool Query(Mat img, Size templateSize)
    {
        if (img == null || prevImg == null) return false;

        // 模板变了(不同文本/不同尺寸)就不能复用上一帧的分数——即使搜索图相同
        if (templateSize != prevTemplateSize) return false;

        // treat two empty mat as identical as well
        if (img.IsEmpty && prevImg.IsEmpty) return true;
        // if dimensionality of two mat is not identical, these two mat is not identical
        if (img.Cols != prevImg.Cols || img.Rows != prevImg.Rows || img.Dims != prevImg.Dims) return false;

        CvInvoke.Compare(img, prevImg, diffMat, CmpType.NotEqual);
        var diffPx = CvInvoke.CountNonZero(diffMat);

        if (diffPx > 0) return false;

        return true;
    }

    private void Reset()
    {
        prevImg = null;
    }
}