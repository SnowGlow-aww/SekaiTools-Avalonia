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
}
