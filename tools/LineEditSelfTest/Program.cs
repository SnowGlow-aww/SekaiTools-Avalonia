using SekaiToolsBase.Story.StoryEvent;
using SekaiToolsBase.Utils;
using SekaiToolsCore.Process.FrameSet;
using SekaiToolsCore.Process.Model;

var failures = 0;

void Check<T>(string name, T actual, T expected)
{
    if (EqualityComparer<T>.Default.Equals(actual, expected)) return;
    Console.Error.WriteLine($"[FAIL] {name}: expected {expected}, got {actual}");
    failures++;
}

var cases = new (string Name, string Text, int? Expected)[]
{
    ("literal-N", "前半\\N后半", 2),
    ("literal-n", "abc\\ndef", 3),
    ("real-newline", "前半\n后半", 2),
    // \R 是专用时间分轴点；同一文本中更早的 \N 只是排版换行。
    ("R-over-N", "甲\\N乙\\R丙", 2),
    ("none", "没有分轴", null),
};

foreach (var c in cases)
    Check(c.Name, c.Text.ExplicitSeparatorContentIndex(), c.Expected);
Check("trim-literal-n", "a\\nb".TrimAll().Length, 2);

DialogBaseFrameSet MakeSet(string translation)
{
    var data = new DialogStoryEvent(0, "一\n二\n三", 0, "测试", false, false)
    {
        BodyTranslated = translation,
    };
    return new DialogBaseFrameSet(data, FrameRate.Fps60);
}

// 覆盖旧代码把真实换行误写成 IndexOf("\\R")、最终得到 -1 的回归。
var actualNewline = MakeSet("前半\n后半");
Check("constructor-real-newline-index", actualNewline.Separate.SeparatorContentIndex, 2);
Check("constructor-real-newline-enabled", actualNewline.UseSeparator, false);
Check("constructor-literal-N-enabled", MakeSet("前半\\N后半").UseSeparator, false);

// 模拟截图：先有旧分割点，再把译文改为在更后面的 \N 处断开；新值必须覆盖旧值。
var edited = MakeSet("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCD");
edited.SetSeparator(200, 7);
edited.ApplyTranslation("ABCDEFGHIJ\\NKLMNOPQRSTUVWXYZ0123456789", true);
Check("edited-literal-N-index", edited.Separate.SeparatorContentIndex, 10);
Check("edited-literal-N-frame-preserved", edited.Separate.SeparateFrame, 200);
Check("edited-literal-N-enabled", edited.UseSeparator, true);

// UI 明确关闭分轴时优先于文本自动判断。
edited.ApplyTranslation("ABCDEFGHIJ\\NKLMNOPQRSTUVWXYZ0123456789", false);
Check("explicit-disable-preserved", edited.UseSeparator, false);

// 首尾空行不是有效文本边界，不能被 Math.Clamp 偷偷改成第一个/最后一个字处断开。
var boundary = MakeSet("ABCDEFGHIJ");
boundary.SetSeparator(200, 4);
boundary.ApplyTranslation("\\NABCDEFGHIJ", true);
Check("leading-marker-keeps-index", boundary.Separate.SeparatorContentIndex, 4);
boundary.ApplyTranslation("ABCDEFGHIJ\\N", true);
Check("trailing-marker-keeps-index", boundary.Separate.SeparatorContentIndex, 4);

if (failures > 0) return 1;
Console.WriteLine("[PASS] line edit separator indexes and explicit split state");
return 0;
