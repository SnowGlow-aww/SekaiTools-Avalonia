using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;

namespace SekaiToolsCore.Process.Model;

public class GaMat : IDisposable // Gray and Alpha Mat
{
    public readonly Mat Alpha;
    public readonly Mat Gray;

    public GaMat(IInputArray src, bool resize = true)
    {
        var grayImage = new Mat();
        var alphaChannel = new Mat();
        CvInvoke.CvtColor(src, grayImage, ColorConversion.Bgra2Gray);
        CvInvoke.ExtractChannel(src, alphaChannel, 3);
        // 还原 1.3.3：保留抗锯齿的软 alpha 作为掩膜权重，不做 127 二值化。
        // 模板渲染几何已忠实复刻旧版后，软 alpha 与旧版掩膜覆盖一致，匹配峰值更高更稳。
        if (resize)
        {
            const int scaleRatio = 5;
            var size = new Size(grayImage.Size.Width / scaleRatio, grayImage.Size.Height / scaleRatio);
            CvInvoke.Resize(grayImage, grayImage, size);
            CvInvoke.Resize(alphaChannel, alphaChannel, size);
        }

        Gray = grayImage;
        Alpha = alphaChannel;
    }

    public Size Size => Gray.Size;

    // Releases the two native Mats this wrapper owns. Cached GaMats (TemplateManager's
    // GaMat cache) live for the whole run and are not disposed per-call; only one-shot
    // GaMats created outside the cache (e.g. SubtitleMaker name-tag width probes) are
    // wrapped in `using` so they don't accumulate.
    public void Dispose()
    {
        Gray.Dispose();
        Alpha.Dispose();
    }
}