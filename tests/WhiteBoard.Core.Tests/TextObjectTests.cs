using WhiteBoard.Core.Commands;
using WhiteBoard.Core.Geometry;
using WhiteBoard.Core.Model;
using WhiteBoard.Core.Storage;

namespace WhiteBoard.Core.Tests;

/// <summary>
/// 文本对象的模型语义：不参与擦除、尺寸由外部测量填入、指纹随文字变化、
/// 以及"改文字"命令的撤销/重做与文件往返。
/// </summary>
public static class TextObjectTests
{
    private static TextObject NewText(WhiteboardDocument doc, string text = "白板", double size = 36)
    {
        // Core 不做文字测量，这里用一个确定的近似尺寸（模拟渲染层测出来的结果）
        var w = text.Length * size * 0.6;
        return TextObject.FromWorldTopLeft(
            doc.AllocateObjectId(), text, new PointD(100, 200), w, size * 1.35, size, "#F5F5F0");
    }

    public static void Test_Text_BasicProperties()
    {
        var doc = new WhiteboardDocument { Id = 1 };
        doc.EnsureAtLeastOnePage();
        var t = NewText(doc, "白板测试", 36);

        Check.Equal("text", t.Kind, "类型判别符应为 text");
        Check.Equal("白板测试", t.Text, "内容应保留");
        Check.Near(36, t.FontSize, 1e-9, "字号应保留");
        Check.Equal(1, t.LineCount, "单行文本行数应为 1");
        Check.Near(100, t.X, 1e-9, "世界坐标 X 应为文本框左上角");
        Check.True(t.LocalWidth > 1 && t.LocalHeight > 1, "尺寸应来自测量结果");
    }

    /// <summary>基线要求：擦除时跳过文本（要删文字用选择工具 + Del）。</summary>
    public static void Test_Text_IsNotErasable()
    {
        var doc = new WhiteboardDocument { Id = 1 };
        doc.EnsureAtLeastOnePage();
        var t = NewText(doc);

        Check.True(!t.IsErasable, "文本不应参与橡皮擦除（基线要求）");
        Check.True(t.AddErasure(new EraserCircle(1, 1, 5)) || t.Erasures.Count == 0,
            "文本上的擦除遮罩不会被使用（IsErasable=false 时橡皮直接跳过）");
    }

    public static void Test_Text_MultiLineAndFirstLineSummary()
    {
        var doc = new WhiteboardDocument { Id = 1 };
        doc.EnsureAtLeastOnePage();
        var t = NewText(doc, "第一行\n第二行\n第三行");

        Check.Equal(3, t.LineCount, "应识别 3 行");
        Check.Equal("第一行", t.FirstLine, "摘要应是首行");

        var longLine = NewText(doc, new string('字', 60));
        Check.True(longLine.FirstLine.EndsWith("…"), "过长的首行应被截断显示");
    }

    /// <summary>改了文字，几何指纹必须变，否则渲染缓存不失效 → 屏幕上还是旧文字。</summary>
    public static void Test_Text_FingerprintChangesWithContent()
    {
        var doc = new WhiteboardDocument { Id = 1 };
        doc.EnsureAtLeastOnePage();
        var t = NewText(doc, "甲");

        var f1 = t.GeometryFingerprint;
        t.Text = "乙";
        var f2 = t.GeometryFingerprint;
        Check.True(f1 != f2, "改文字后指纹应变化（否则会显示旧文字）");

        t.Bold = true;
        Check.True(t.GeometryFingerprint != f2, "改粗体后指纹也应变化");
    }

    public static void Test_Text_CloneIsDeepCopy()
    {
        var doc = new WhiteboardDocument { Id = 1 };
        doc.EnsureAtLeastOnePage();
        var t = NewText(doc, "原始");
        t.Bold = true;

        var c = (TextObject)t.Clone(999);
        Check.Equal(999, c.Id, "副本应有新 Id");
        Check.Equal("原始", c.Text, "内容应复制");
        Check.True(c.Bold, "粗体应复制");

        c.Text = "改过了";
        Check.Equal("原始", t.Text, "改副本不应影响原对象");
    }

    // ── 命令 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 改文字必须**连尺寸一起改**：否则选中框、命中测试、视口裁剪都会用旧尺寸，
    /// 表现是"选中框比文字小一圈""点了文字却选不中"。
    /// </summary>
    public static void Test_SetTextCommand_UndoRestoresTextAndSize()
    {
        var doc = new WhiteboardDocument { Id = 1 };
        var page = doc.EnsureAtLeastOnePage();
        var t = NewText(doc, "短");
        page.Add(t);

        var oldW = t.LocalWidth;
        var oldH = t.LocalHeight;

        var cmd = new SetTextCommand(page, t, "变得很长的一段文字", 400, 50);

        cmd.Do();
        Check.Equal("变得很长的一段文字", t.Text, "应写入新文字");
        Check.Near(400, t.LocalWidth, 1e-9, "宽度应更新为新测量值");
        Check.Near(50, t.LocalHeight, 1e-9, "高度应更新为新测量值");

        cmd.Undo();
        Check.Equal("短", t.Text, "撤销应还原文字");
        Check.Near(oldW, t.LocalWidth, 1e-9, "撤销应还原宽度");
        Check.Near(oldH, t.LocalHeight, 1e-9, "撤销应还原高度");

        cmd.Do();
        Check.Equal("变得很长的一段文字", t.Text, "重做应再次生效");
        Check.Near(400, t.LocalWidth, 1e-9, "重做应再次更新尺寸");
    }

    public static void Test_SetTextCommand_IsOnPageStackAndUndoable()
    {
        var doc = new WhiteboardDocument { Id = 1 };
        var page = doc.EnsureAtLeastOnePage();
        var t = NewText(doc, "甲");
        page.Add(t);

        var commands = new CommandManager();
        commands.SetCurrentPage(page.Id);

        commands.Execute(new SetTextCommand(page, t, "乙", 20, 30));
        Check.Equal("乙", t.Text, "已改为乙");
        Check.True(commands.CanUndo, "应可撤销");

        commands.Undo();
        Check.Equal("甲", t.Text, "撤销后回到甲");

        commands.Redo();
        Check.Equal("乙", t.Text, "重做后又是乙");
    }

    public static void Test_SetTextStyleCommand_UndoRestoresStyleAndSize()
    {
        var doc = new WhiteboardDocument { Id = 1 };
        doc.EnsureAtLeastOnePage();
        var t = NewText(doc, "样式", 24);

        var oldW = t.LocalWidth;
        var cmd = new SetTextStyleCommand(t, 56, bold: true, italic: false, newWidth: 500, newHeight: 80);

        cmd.Do();
        Check.Near(56, t.FontSize, 1e-9, "字号应变大");
        Check.True(t.Bold, "应变粗体");

        cmd.Undo();
        Check.Near(24, t.FontSize, 1e-9, "撤销应还原字号");
        Check.True(!t.Bold, "撤销应还原粗体");
        Check.Near(oldW, t.LocalWidth, 1e-9, "撤销应还原宽度");
    }

    // ── 文件往返 ──────────────────────────────────────────────────────────

    public static void Test_Text_SurvivesWbRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-text-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "文本.wb");

        var doc = new WhiteboardDocument { Id = 1 };
        var page = doc.EnsureAtLeastOnePage();

        var t = NewText(doc, "第一行\n第二行：中文、English、123", 44);
        t.Bold = true;
        t.Italic = true;
        t.FontFamily = "SimSun";
        t.ZIndex = 7;
        page.Add(t);
        page.Add(NewText(doc, "第二段文字", 24));

        WbPackage.Save(doc, file);
        var result = WbPackage.Load(file);
        var loaded = result.Document;

        Check.True(!result.HasWarnings, $"不应有警告：{result.WarningText}");
        Check.Equal(2, loaded.Pages[0].Count, "两个文本对象都应读回");

        var back = (TextObject)loaded.Pages[0].Objects.Single(o => o.Id == t.Id);
        Check.Equal(t.Text, back.Text, "文本内容（含换行与中英混排）应一致");
        Check.Near(t.FontSize, back.FontSize, 1e-9, "字号应一致");
        Check.Equal("SimSun", back.FontFamily, "字体族应一致");
        Check.True(back.Bold && back.Italic, "粗体/斜体应一致");
        Check.Near(t.X, back.X, 1e-3, "位置应一致");
        Check.Near(t.LocalWidth, back.LocalWidth, 1e-3, "宽度应一致（不重新测量）");
        Check.Equal(7, back.ZIndex, "Z 序应一致");
        Check.True(!back.IsErasable, "文本不可擦的属性应保持");

        Directory.Delete(dir, true);
    }

    /// <summary>空文本也要能往返（不丢数据），只是渲染出来是空的。</summary>
    public static void Test_Text_EmptyTextSurvivesRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-text2-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "空文本.wb");

        var doc = new WhiteboardDocument { Id = 1 };
        var page = doc.EnsureAtLeastOnePage();
        page.Add(NewText(doc, ""));

        WbPackage.Save(doc, file);
        var result = WbPackage.Load(file);
        var loaded = result.Document;

        Check.Equal(1, loaded.Pages[0].Count, "空文本对象不应被丢掉");
        Check.Equal("", ((TextObject)loaded.Pages[0].Objects[0]).Text, "内容应为空字符串");
        Check.True(!result.HasWarnings, $"不应产生误导性警告：{result.WarningText}");

        Directory.Delete(dir, true);
    }
}
