using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using SekaiToolsCore.Process.Model;
using SkiaSharp;

namespace SekaiToolsCore.Match.TemplateMatcher;

public enum TemplateUsage
{
    DialogNameTag,
    DialogContent,
    BannerContent,
    MarkerContent
}

/// <summary>
/// 使用 SkiaSharp 渲染文本模板图像，供模板匹配使用。
/// </summary>
public class TemplateManager(Size videoResolution, bool noScale = false)
{
    private const string MenuSignBase = "menu-107px.png";
    private const string DbFontBase = "FOT-RodinNTLGPro-DB.otf";
    private const string EbFontBase = "FOT-RodinNTLGPro-EB.otf";

    private readonly Dictionary<TemplateUsage, Dictionary<string, Mat>?> _template = new();
    private readonly Dictionary<(TemplateUsage Usage, string Text), GaMat> _gaTemplate = new();
    private readonly Dictionary<string, SKTypeface> _typefaceCache = new();

    private Mat? _menuSign;

    public Mat GetMenuSign()
    {
        if (_menuSign != null) return _menuSign;
        var menuTemplatePath = ResourceManager.Instance.ResourcePath(ResourceType.VideoProcess, MenuSignBase);
        if (!File.Exists(menuTemplatePath)) throw new FileNotFoundException();
        var menuTemplate = CvInvoke.Imread(menuTemplatePath, ImreadModes.Unchanged)!;
        var menuSize = GetMenuSignSize(videoResolution);

        CvInvoke.Resize(menuTemplate, menuTemplate, new Size(menuSize, menuSize));
        _menuSign = menuTemplate;
        return menuTemplate;
    }

    public static int GetMenuSignSize(Size videoSize)
    {
        // 还原 1.3.3：仅超竖屏(高/宽 > 16/9)按高度系数，其余(含横屏与恰为 16:9)按宽度系数。
        if (videoSize.Height / (double)videoSize.Width > 16.0 / 9.0)
            return (int)(videoSize.Height * 0.0741);
        return (int)(videoSize.Width * 0.0417);
    }

    public static int GetFontSize(Size videoSize, double scale = 0.95)
    {
        const double standardRatio = 16.0 / 9.0;
        var ratio = videoSize.Width / (double)videoSize.Height;
        var size = ratio switch
        {
            < standardRatio => (int)(videoSize.Width * 0.024),
            _ => (int)(videoSize.Height * 0.043)
        };
        var result = (int)(size * scale);
        return result;
    }

    public int GetFontSize(double fontScale = 0.95)
    {
        // 还原 1.3.3 的渲染字号：单次取整 + 旧版分支(恰为 16:9 时按宽度系数)。
        // 注意：这是模板"渲染"用字号；字幕"定位"偏移仍走静态 GetFontSize(Size,scale)，不受影响。
        const double standardRatio = 16.0 / 9.0;
        var ratio = videoResolution.Width / (double)videoResolution.Height;
        var baseSize = ratio > standardRatio
            ? videoResolution.Height * 0.043
            : videoResolution.Width * 0.024;
        var scale = (noScale ? 1 : 5) * fontScale;
        return (int)(baseSize * scale);
    }

    private static Mat CropByAlpha(Mat bgra)
    {
        using var alpha = new Mat();
        CvInvoke.ExtractChannel(bgra, alpha, 3);
        using var binary = new Mat();
        CvInvoke.Threshold(alpha, binary, 1, 255, ThresholdType.Binary);
        var rect = CvInvoke.BoundingRectangle(binary);
        if (rect.Width == 0 || rect.Height == 0)
            return bgra.Clone();
        return new Mat(bgra, rect).Clone();
    }

    private SKTypeface GetTypeface(string fontFilePath)
    {
        if (_typefaceCache.TryGetValue(fontFilePath, out var cached)) return cached;
        var typeface = SKTypeface.FromFile(fontFilePath)
                       ?? throw new InvalidOperationException($"Failed to load typeface: {fontFilePath}");
        _typefaceCache[fontFilePath] = typeface;
        return typeface;
    }

    private SKTypeface GetDbTypeface()
    {
        var fontFilePath = ResourceManager.Instance.ResourcePath(ResourceType.VideoProcess, DbFontBase);
        return GetTypeface(fontFilePath);
    }

    private SKTypeface GetEbTypeface()
    {
        var fontFilePath = ResourceManager.Instance.ResourcePath(ResourceType.VideoProcess, EbFontBase);
        return GetTypeface(fontFilePath);
    }

    private Mat CreateImageWithText(TemplateUsage usage, string text)
    {
        // 还原 1.3.3：DB / EB 字体统一用 0.95 字号系数(旧版对所有用例都用同一个 GetFontSize)。
        var (typeface, fontSizeScale) = usage switch
        {
            TemplateUsage.DialogNameTag => (GetEbTypeface(), 0.95),
            TemplateUsage.DialogContent or TemplateUsage.BannerContent or TemplateUsage.MarkerContent
                => (GetDbTypeface(), 0.95),
            _ => throw new ArgumentOutOfRangeException(nameof(usage), usage, null)
        };

        var fontSize = GetFontSize(fontSizeScale);

        // 还原 1.3.3 的 byChar 逐字定宽排版：DB 字体(对话内容/横幅/地标)且文本不含半角字母数字时，
        // 每个字按固定全角字距 x = 10 + fontSize * 1.01 * i 单独绘制，与游戏内 CJK 排版一致；
        // 名牌(EB)或含字母数字的文本则整串绘制(走字体自带字距)。
        var byChar = usage != TemplateUsage.DialogNameTag && !ContainsAlphanumeric(text);

        var canvasWidth = (int)(text.Length * fontSize * 2);
        var canvasHeight = fontSize * 2;

        var info = new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(SKColors.Transparent);

        var (fillColor, strokeColor, strokeWidth, withStroke) = ResolvePaints(fontSize);

        using (var canvas = new SKCanvas(bitmap))
        {
            using var textPaint = new SKPaint
            {
                Typeface = typeface,
                TextSize = fontSize,
                IsAntialias = true,
            };
            using var strokePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
                StrokeWidth = strokeWidth,
                Color = strokeColor,
                IsAntialias = true,
            };
            using var fillPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = fillColor,
                IsAntialias = true,
            };

            var originY = 10f + fontSize;
            if (byChar)
            {
                for (var i = 0; i < text.Length; i++)
                {
                    // 与旧版一致：逐字定宽，先描边后填充。
                    var originX = (int)(10 + fontSize * 1.01 * i);
                    using var glyphPath = textPaint.GetTextPath(text[i].ToString(), originX, originY);
                    if (withStroke) canvas.DrawPath(glyphPath, strokePaint);
                    canvas.DrawPath(glyphPath, fillPaint);
                }
            }
            else
            {
                const float originX = 10f;
                using var textPath = textPaint.GetTextPath(text, originX, originY);
                if (withStroke) canvas.DrawPath(textPath, strokePaint);
                canvas.DrawPath(textPath, fillPaint);
            }
        }

        // 横幅与其它用例一致：透明底 + 描边字形，alpha 掩膜只含字形像素(不再叠不透明灰底块)，
        // 这样模板匹配只相关字形笔画、对横幅多变背景不敏感。
        using var skMat = SkBitmapToBgraMat(bitmap);
        return CropByAlpha(skMat);
    }

    private static bool ContainsAlphanumeric(string text)
    {
        foreach (var c in text)
            if (char.IsAsciiLetterOrDigit(c))
                return true;
        return false;
    }

    private static (SKColor fill, SKColor stroke, float strokeWidth, bool withStroke) ResolvePaints(float fontSize)
    {
        // 还原 1.3.3：纯白(255)填充 + 灰(64)描边，描边宽度 = 字号 / 6，所有用例都描边。
        var fill = new SKColor(255, 255, 255, 255);
        var stroke = new SKColor(64, 64, 64, 255);
        return (fill, stroke, fontSize / 6f, true);
    }

    private static Mat SkBitmapToBgraMat(SKBitmap bitmap)
    {
        var src = new Mat(bitmap.Height, bitmap.Width, DepthType.Cv8U, 4, bitmap.GetPixels(), bitmap.RowBytes);
        try
        {
            return src.Clone();
        }
        finally
        {
            src.Dispose();
        }
    }

    public Mat GetTemplate(TemplateUsage usage, string text)
    {
        var usageDict = _template.GetValueOrDefault(usage);
        if (usageDict == null) _template[usage] = usageDict = new Dictionary<string, Mat>();

        if (usageDict.TryGetValue(text, out var template)) return template;

        var mat = CreateImageWithText(usage, text);
        usageDict[text] = mat;
        return mat;
    }

    // Cached GaMat (Gray+Alpha) wrapper over a template. The recognition hot path builds
    // the same few templates every frame; before this cache each frame allocated fresh
    // Gray+Alpha Mats that were never disposed (the dominant per-frame native leak). The
    // GaMat only reads its source Mat (CvtColor/ExtractChannel), so it is safe to derive
    // from the shared _template Mat. Keyed identically to _template; same bounded size.
    //
    // Recognition-thread only: all callers (Dialog/Banner matchers) run on the single
    // recognition thread. The export path (SubtitleMaker) deliberately does NOT use this
    // cache — it builds one-shot `using` GaMats — so this dictionary needs no locking.
    public GaMat GetGaTemplate(TemplateUsage usage, string text)
    {
        var key = (usage, text);
        if (_gaTemplate.TryGetValue(key, out var cached)) return cached;

        var ga = new GaMat(GetTemplate(usage, text));
        _gaTemplate[key] = ga;
        return ga;
    }
}
