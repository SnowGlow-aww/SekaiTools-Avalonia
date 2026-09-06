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
        Misc = 7,
        ProbeNameTag = 8,
        ProbeContent = 9
    }

    private static volatile List<TemplateMatchCachePool>? _globalPool;
    private static readonly object _globalGate = new();
    public Mat diffMat;

    public Mat? prevImg;
    // Whether this pool owns prevImg (i.e. is responsible for disposing it). True for the
    // normal grayscale-copy path; false only if a caller ever hands us an aliased frame
    // (then RegisterResult clones, so the pool always ends up owning its prevImg).
    private bool _ownsPrevImg;
    public Size prevTemplateSize;
    private TemplateMatchResult prevResult;

    // Serializes the read of prevImg (Query's CvInvoke.Compare) against its disposal
    // (RegisterResult / Reset). Normally this pool is touched by a single recognition
    // thread and the lock is uncontended (~ns, negligible next to MatchTemplate, which
    // runs OUTSIDE it). It exists for the one adversarial case: if a previous run's
    // recognition thread stalls past VideoProcessor.Dispose's 10s Wait, ResetAll rebuilds
    // this static pool, and the unblocked "zombie" re-enters GetPool and shares these very
    // instances with the new run's thread — without the lock the zombie could dispose
    // prevImg while the new thread reads its native buffer (cross-thread use-after-free).
    private readonly object _gate = new();

    public TemplateMatchCachePool()
    {
        diffMat = new Mat();
    }

    private static List<TemplateMatchCachePool> GlobalPool
    {
        get
        {
            var pool = _globalPool;
            if (pool != null) return pool;
            // Double-checked lock: build the full list into a local and publish it to the
            // volatile field only AFTER every slot exists, so a concurrent GetPool (e.g. a
            // zombie recognition thread racing the new run's thread through ResetAll) can
            // never observe a half-filled list and index past its end (IndexOutOfRange).
            lock (_globalGate)
            {
                if (_globalPool != null) return _globalPool;
                const int len = (int)MatchUsage.ProbeContent + 1;
                var built = new List<TemplateMatchCachePool>(len);
                for (var i = 0; i < len; i++) built.Add(new TemplateMatchCachePool());
                _globalPool = built;
                return built;
            }
        }
    }

    public static TemplateMatchCachePool GetPool(MatchUsage usage)
    {
        return GlobalPool[(int)usage];
    }

    // Drop the process-wide pool so a new run starts with no carried-over prevImg/
    // prevResult from the previous video. The old pool instances become unrooted and
    // are reclaimed by GC (their owned prevImg/diffMat finalize with them); the next
    // GetPool rebuilds a fresh set. Called at每次开新打轴.
    //
    // Deliberately does NOT dispose prevImg eagerly: ResetAll runs on the scheduler
    // thread after VideoProcessor.Dispose, whose ProcessingTask.Wait has a 10s timeout.
    // A timed-out zombie recognition thread could still be inside Query reading prevImg,
    // and ResetAll holds no per-pool _gate to serialize against it, so a Dispose here
    // could UAF. At most 8 small grayscale Mats per run are left to GC — negligible.
    // The per-frame ownership below (RegisterResult/Reset) is what actually stops the
    // leak, and its dispose/read are _gate-guarded so they stay safe even if that zombie
    // shares the rebuilt pool.
    public static void ResetAll()
    {
        lock (_globalGate)
        {
            _globalPool = null;
        }
    }

    public static void NextDialog()
    {
        GlobalPool[(int)MatchUsage.DialogNameTag].Reset();
        GlobalPool[(int)MatchUsage.DialogContent1].Reset();
        GlobalPool[(int)MatchUsage.DialogContent2].Reset();
        GlobalPool[(int)MatchUsage.DialogContent3].Reset();
        GlobalPool[(int)MatchUsage.ProbeNameTag].Reset();
        GlobalPool[(int)MatchUsage.ProbeContent].Reset();
        GlobalPool[(int)MatchUsage.Misc].Reset();
    }

    // Takes over caching of this frame's search image, disposing the previous prevImg it
    // owned — this is what plugs the per-call leak of the old grayscale copy. The dispose
    // + swap is under _gate so it can't race a concurrent Query reading prevImg (see the
    // _gate comment). transferOwnership==false means `img` aliases the caller's frame
    // (non-3-channel path); we clone (outside the lock, it's thread-local) so the pool
    // never dangles a Mat it doesn't own.
    public void RegisterResult(Mat img, Size templateSize, TemplateMatchResult result,
        bool transferOwnership)
    {
        var owned = transferOwnership ? img : img.Clone();
        lock (_gate)
        {
            if (_ownsPrevImg) prevImg?.Dispose();
            prevImg = owned;
            _ownsPrevImg = true;
            prevTemplateSize = templateSize;
            prevResult = result;
        }
    }

    // True iff `img` is pixel-identical to the cached prevImg (so the caller may reuse
    // cachedResult without re-matching). Body is under _gate: prevImg's native buffer is
    // read here (CvInvoke.Compare) and freed in RegisterResult/Reset, so the two must be
    // mutually exclusive; cachedResult is copied out under the lock too so a concurrent
    // RegisterResult can't tear it.
    public bool Query(Mat img, Size templateSize, out TemplateMatchResult cachedResult)
    {
        lock (_gate)
        {
            cachedResult = prevResult;
            if (img == null || prevImg == null) return false;

            // 模板变了(不同文本/不同尺寸)就不能复用上一帧的分数——即使搜索图相同
            if (templateSize != prevTemplateSize) return false;

            // treat two empty mat as identical as well
            if (img.IsEmpty && prevImg.IsEmpty) return true;
            // if dimensionality of two mat is not identical, these two mat is not identical
            if (img.Cols != prevImg.Cols || img.Rows != prevImg.Rows || img.Dims != prevImg.Dims) return false;

            CvInvoke.Compare(img, prevImg, diffMat, CmpType.NotEqual);
            return CvInvoke.CountNonZero(diffMat) == 0;
        }
    }

    private void Reset()
    {
        // Disposing the owned prevImg here (NextDialog boundary) prevents it leaking across
        // dialogs; under _gate so it can't race a Query on the same pool.
        lock (_gate)
        {
            if (_ownsPrevImg) prevImg?.Dispose();
            prevImg = null;
            _ownsPrevImg = false;
        }
    }
}