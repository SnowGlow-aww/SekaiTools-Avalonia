namespace SekaiToolsBase.Utils;

public static class StringFunc
{
    public static int LineCount(this string str)
    {
        return str.Split('\n').Select(value => value.Length > 0 ? 1 : 0).Sum();
    }

    public static int Count(this string str, string part)
    {
        var count = 0;
        var i = 0;
        while ((i = str.IndexOf(part, i, StringComparison.Ordinal)) != -1)
        {
            i += part.Length;
            count++;
        }

        return count;
    }

    public static string TrimAll(this string str)
    {
        return str.Trim().Replace("\n", "")
            .Replace("\\R", "")
            .Replace("\\N", "")
            .Replace("\\n", "");
    }

    /// <summary>
    /// 返回译文里显式分轴标记之前的净文本长度（即 SeparatorContentIndex 的口径）。
    /// \R 是专用的时间分轴标记，优先级高于同一译文里用于排版的 \N；没有 \R 时，
    /// Web 编辑器输入的字面 \N/\n 与桌面 QuickEdit 产生的真实换行都可作为分轴点。
    /// </summary>
    public static int? ExplicitSeparatorContentIndex(this string str)
    {
        if (string.IsNullOrEmpty(str)) return null;

        var returnMarker = str.IndexOf("\\R", StringComparison.Ordinal);
        if (returnMarker >= 0)
            return str[..returnMarker].TrimAll().Length;

        var first = int.MaxValue;
        var newline = str.IndexOf('\n');
        if (newline >= 0) first = Math.Min(first, newline);

        foreach (var marker in new[] { "\\N", "\\n" })
        {
            var position = str.IndexOf(marker, StringComparison.Ordinal);
            if (position >= 0) first = Math.Min(first, position);
        }

        return first == int.MaxValue ? null : str[..first].TrimAll().Length;
    }

    public static string EscapedReturn(this string str)
    {
        return str.Replace("\\N", "\n")
            .Replace("\\R", "\n");
    }

    public static int MaxLineLength(this string str)
    {
        return str.Split('\n').Max(x => x.Trim().Length);
    }

    /// <summary>
    /// 计算文本的视觉加权长度（全角汉字/假名/全角符号=1.0，半角ASCII/英文字母/半角数字/半角标点/空格=0.5）。
    /// 自动剔除换行符与转义标记。
    /// </summary>
    public static double VisualWeight(this string str)
    {
        if (string.IsNullOrEmpty(str)) return 0;
        var clean = str.TrimAll();
        var weight = 0.0;
        foreach (var c in clean)
        {
            weight += char.IsAscii(c) ? 0.5 : 1.0;
        }
        return weight;
    }

    /// <summary>
    /// 获取多行文本中单行的最大视觉加权长度。
    /// </summary>
    public static double MaxLineVisualWeight(this string str)
    {
        if (string.IsNullOrEmpty(str)) return 0;
        var lines = str.Split(new[] { "\\N", "\\n", "\n" }, StringSplitOptions.None);
        if (lines.Length == 0) return 0;
        return lines.Max(l =>
        {
            var clean = l.Trim().Replace("\\R", "");
            var w = 0.0;
            foreach (var c in clean)
            {
                w += char.IsAscii(c) ? 0.5 : 1.0;
            }
            return w;
        });
    }

    /// <summary>
    /// 为超长单行文本寻找最佳自然分轴切分点（以 TrimAll 为基准的字符索引）。
    /// 优先在靠近权重中点的标点后断开；若无标点则优先在半角空格（英文单词边界）断开，
    /// 杜绝将英文单词横向劈碎；最后兜底为最靠近权重中点的字符位置。
    /// </summary>
    public static int FindSmartSeparatorContentIndex(this string str)
    {
        var clean = str.TrimAll();
        if (string.IsNullOrEmpty(clean)) return 0;
        if (clean.Length <= 1) return 0;

        var totalWeight = 0.0;
        var weights = new double[clean.Length];
        for (var i = 0; i < clean.Length; i++)
        {
            var w = char.IsAscii(clean[i]) ? 0.5 : 1.0;
            weights[i] = w;
            totalWeight += w;
        }

        var midWeight = totalWeight / 2.0;

        static bool IsPunct(char c) =>
            c is '，' or ',' or '、' or '。' or '.' or '！' or '!' or '？' or '?' or '；' or ';' or '…' or '—' or '~' or '♪';

        var bestIdx = -1;
        var bestDist = double.MaxValue;
        var cumWeight = 0.0;

        // 1. 优先靠近中点的标点后
        for (var i = 0; i < clean.Length - 1; i++)
        {
            cumWeight += weights[i];
            if (IsPunct(clean[i]))
            {
                if (i + 1 < clean.Length && (IsPunct(clean[i + 1]) || clean[i + 1] is '”' or '’' or '）' or '』' or '」'))
                    continue;

                var dist = Math.Abs(cumWeight - midWeight);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i + 1;
                }
            }
        }

        // 2. 其次靠近中点的半角空格（英文单词词界）
        if (bestIdx < 0)
        {
            cumWeight = 0.0;
            for (var i = 0; i < clean.Length - 1; i++)
            {
                cumWeight += weights[i];
                if (clean[i] == ' ')
                {
                    var dist = Math.Abs(cumWeight - midWeight);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIdx = i + 1;
                    }
                }
            }
        }

        // 3. 兜底靠近权重中点的位置
        if (bestIdx < 0)
        {
            cumWeight = 0.0;
            for (var i = 0; i < clean.Length - 1; i++)
            {
                cumWeight += weights[i];
                var dist = Math.Abs(cumWeight - midWeight);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i + 1;
                }
            }
        }

        return bestIdx > 0 ? Math.Clamp(bestIdx, 1, clean.Length - 1) : Math.Max(1, clean.Length / 2);
    }
}
