using WhiteBoard.Core.Model;
using WhiteBoard.Core.Storage;

namespace WhiteBoard.Core.Tests;

/// <summary>
/// 自动保存与崩溃恢复：触发时机、可恢复性判定、只读模式降级。
/// 全部在系统临时目录里跑（把临时目录当成"exe 同级"传给 PathService）。
/// </summary>
public static class AutoSaveTests
{
    private static string TempBase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-autosave-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (PathService Paths, AutoSaveService Auto) NewService(string tempBase)
        => (PathService.Resolve(tempBase), new AutoSaveService(PathService.Resolve(tempBase)));

    // ── 触发时机 ──────────────────────────────────────────────────────────

    /// <summary>刚写完就查 → 不该写盘（书写过程中不能抢 IO，书写是延迟敏感的）。</summary>
    public static void Test_AutoSave_DoesNotFireImmediatelyAfterChange()
    {
        var (_, auto) = NewService(TempBase());
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Check.True(!auto.ShouldSave(now.AddSeconds(-1), now.AddSeconds(-1), now), "刚改完 1 秒不应触发");
        Check.True(!auto.ShouldSave(now.AddSeconds(-10), now.AddSeconds(-10), now), "刚改完 10 秒不应触发");

        // 从未保存过也不例外：第一版也要等用户停下来，不能一开始就抢 IO
        Check.True(!auto.ShouldSave(now.AddSeconds(-20), now.AddSeconds(-1), now),
            "持续书写 20 秒但 1 秒前刚改过，仍不应触发");
    }

    public static void Test_AutoSave_FiresAfterIdleInterval()
    {
        var (_, auto) = NewService(TempBase());
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Check.True(auto.ShouldSave(now.AddSeconds(-30), now.AddSeconds(-30), now),
            "静置 30 秒应触发（阈值 25 秒）");
    }

    /// <summary>一直在写（改动不断）也必须偶尔落盘，否则一次崩溃丢掉全部内容。</summary>
    public static void Test_AutoSave_FiresAfterMaxIntervalEvenWhileWriting()
    {
        var (_, auto) = NewService(TempBase());
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        // 连续书写：从 t0 开始一直有改动（最近一次改动只比现在早 1 秒）
        Check.True(!auto.ShouldSave(t0, t0.AddSeconds(30).AddSeconds(-1), t0.AddSeconds(30)),
            "连续书写 30 秒（未到 90 秒上限）不应打断");
        Check.True(auto.ShouldSave(t0, t0.AddSeconds(120).AddSeconds(-1), t0.AddSeconds(120)),
            "连续书写超过 90 秒必须强制落一版");

        // 保存之后，计时重置，不能因为"上次保存很早"而立刻又存
        var doc = new WhiteboardDocument { Id = 1 };
        doc.EnsureAtLeastOnePage();
        Check.True(auto.Save(doc, null, null, null, t0.AddSeconds(120)), "强制落盘应成功");
        Check.True(!auto.ShouldSave(t0.AddSeconds(120), t0.AddSeconds(121), t0.AddSeconds(122)),
            "刚落盘后又开始写，不应立即重复落盘");
    }

    public static void Test_AutoSave_NeverFiresWithoutChange()
    {
        var (_, auto) = NewService(TempBase());
        var now = DateTime.UtcNow;

        Check.True(!auto.ShouldSave(DateTime.MinValue, now, now), "没有任何改动时不应触发");
        Check.True(!auto.ShouldSave(DateTime.MinValue, DateTime.MinValue, now), "无改动时间戳时不应触发");
    }

    // ── 写入与恢复 ────────────────────────────────────────────────────────

    public static void Test_AutoSave_WritesRecoverableContent()
    {
        var baseDir = TempBase();
        var (paths, auto) = NewService(baseDir);

        var doc = new WhiteboardDocument { Id = 1 };
        var page = doc.EnsureAtLeastOnePage();
        page.Add(RectObject.FromWorldCorners(1, new Geometry.PointD(10, 10), new Geometry.PointD(80, 60), "#FFFFFF", 4));

        Check.True(auto.Save(doc, @"D:\某个\用户文件.wb", null, "test"), "自动保存应成功");
        Check.True(File.Exists(auto.AutoSaveFile), "自动保存文件应存在");
        Check.True(auto.AutoSaveFile.StartsWith(paths.DataRoot, StringComparison.OrdinalIgnoreCase),
            "自动保存必须落在 data\\ 内（C3）");

        var info = auto.Inspect();
        Check.True(info is not null, "应能检查到可恢复的自动保存");
        Check.Equal(@"D:\某个\用户文件.wb", info!.Meta.SourcePath, "应记住原文件路径（含中文）");
        Check.Equal(1, info.Meta.PageCount, "页数应记录");
        Check.Equal(1, info.Meta.ObjectCount, "对象数应记录");

        // 内容真的能作为画板打开
        var loaded = WbPackage.Load(auto.AutoSaveFile);
        Check.Equal(1, loaded.Document.Pages[0].Count, "恢复出来的内容应包含那个对象");

        Directory.Delete(baseDir, true);
    }

    /// <summary>原文件比自动保存新 → 不该提示恢复（用户自己存过了）。</summary>
    public static void Test_AutoSave_NotNewerWhenSourceSavedLater()
    {
        var baseDir = TempBase();
        var (_, auto) = NewService(baseDir);

        var sourceFile = Path.Combine(baseDir, "user.wb");
        var doc = new WhiteboardDocument { Id = 1 };
        doc.EnsureAtLeastOnePage();

        auto.Save(doc, sourceFile, null, null, DateTime.UtcNow.AddMinutes(-10));
        File.WriteAllText(sourceFile, "占位");   // 模拟"用户之后又保存了"

        var info = auto.Inspect();
        Check.True(info is not null, "应能检查到自动保存");
        Check.True(!info!.NewerThanSource, "原文件更新时间更晚时不应提示恢复");

        Directory.Delete(baseDir, true);
    }

    public static void Test_AutoSave_ReportsSourceMissing()
    {
        var baseDir = TempBase();
        var (_, auto) = NewService(baseDir);

        var doc = new WhiteboardDocument { Id = 1 };
        doc.EnsureAtLeastOnePage();
        auto.Save(doc, Path.Combine(baseDir, "已删除.wb"), null, null);

        var info = auto.Inspect();
        Check.True(info!.SourceMissing, "原文件不存在时应标记");
        Check.True(info.NewerThanSource, "原文件不存在时应视为有未保存内容");
        Check.True(info.Describe().Contains("原文件已不在"), "描述文字应说明原文件不在了");

        Directory.Delete(baseDir, true);
    }

    public static void Test_AutoSave_ClearRemovesEverything()
    {
        var baseDir = TempBase();
        var (_, auto) = NewService(baseDir);

        var doc = new WhiteboardDocument { Id = 1 };
        doc.EnsureAtLeastOnePage();
        auto.Save(doc, null, null);
        Check.True(auto.Inspect() is not null, "先应有可恢复内容");

        auto.Clear();
        Check.True(auto.Inspect() is null, "清空后不应再提示恢复");
        Check.True(!File.Exists(auto.AutoSaveFile), "文件应被删除");

        Directory.Delete(baseDir, true);
    }

    /// <summary>半残状态（有 wb 没 json，或反过来）都必须被当作"无自动保存"，不能误导用户。</summary>
    public static void Test_AutoSave_HalfWrittenStateIsIgnored()
    {
        var baseDir = TempBase();
        var (_, auto) = NewService(baseDir);

        var doc = new WhiteboardDocument { Id = 1 };
        doc.EnsureAtLeastOnePage();
        auto.Save(doc, null, null);

        File.Delete(auto.MetaFile);
        Check.True(auto.Inspect() is null, "缺 meta 应视为无自动保存");

        Directory.Delete(baseDir, true);
    }

    // ── 最近文件 ──────────────────────────────────────────────────────────

    public static void Test_RecentFiles_AddDedupeAndOrder()
    {
        var baseDir = TempBase();
        var recent = RecentFiles.For(PathService.Resolve(baseDir));

        var a = Path.Combine(baseDir, "a.wb");
        var b = Path.Combine(baseDir, "b.wb");
        var c = Path.Combine(baseDir, "c.wb");
        foreach (var f in new[] { a, b, c }) File.WriteAllText(f, "x");

        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        recent.Add(a, 1, 10, t);
        recent.Add(b, 2, 20, t.AddMinutes(1));
        recent.Add(c, 3, 30, t.AddMinutes(2));
        recent.Add(a, 1, 11, t.AddMinutes(3));   // 重新打开 a → 应置顶且不重复

        var list = recent.Load();
        Check.Equal(3, list.Count, "重复打开不应产生重复条目");
        Check.True(list[0].Path.EndsWith("a.wb"), $"最近打开的应排在首位，实际 {list[0].Name}");
        Check.Equal(11, list[0].ObjectCount, "统计数字应更新为最新一次");

        Directory.Delete(baseDir, true);
    }

    public static void Test_RecentFiles_CapsAtMaxAndPrunesMissing()
    {
        var baseDir = TempBase();
        var recent = RecentFiles.For(PathService.Resolve(baseDir));

        for (var i = 0; i < RecentFiles.MaxEntries + 5; i++)
        {
            var f = Path.Combine(baseDir, $"f{i}.wb");
            File.WriteAllText(f, "x");
            recent.Add(f, 1, i);
        }
        Check.Equal(RecentFiles.MaxEntries, recent.Load().Count, $"最多保留 {RecentFiles.MaxEntries} 条");

        // 删掉文件后，LoadExisting 应把它剔除并回写
        var victim = recent.Load()[0].Path;
        File.Delete(victim);
        var alive = recent.LoadExisting();

        Check.True(alive.All(e => File.Exists(e.Path)), "返回的条目都应真实存在");
        Check.True(recent.Load().All(e => File.Exists(e.Path)), "剔除结果应已回写");
        Check.True(alive.All(e => !string.Equals(e.Path, victim, StringComparison.OrdinalIgnoreCase)),
            "被删掉的条目不应出现");

        Directory.Delete(baseDir, true);
    }

    public static void Test_RecentFiles_SurvivesCorruptFile()
    {
        var baseDir = TempBase();
        var recent = RecentFiles.For(PathService.Resolve(baseDir));

        Directory.CreateDirectory(Path.GetDirectoryName(recent.FilePath)!);
        File.WriteAllText(recent.FilePath, "{ 这不是合法 JSON ");

        Check.Equal(0, recent.Load().Count, "损坏时读空列表而不是抛异常");

        var f = Path.Combine(baseDir, "ok.wb");
        File.WriteAllText(f, "x");
        recent.Add(f, 1, 1);
        Check.Equal(1, recent.Load().Count, "损坏后仍能正常写入新记录");

        Directory.Delete(baseDir, true);
    }
}
