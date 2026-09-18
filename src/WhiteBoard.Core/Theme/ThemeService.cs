using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhiteBoard.Core.Storage;
using WhiteBoard.Core.Text;

namespace WhiteBoard.Core.Theme;

public sealed record ThemeColor(string Id, string Name, string Value);

public sealed record ThemePreset(
    string Id,
    string Name,
    string BackgroundType,
    string Background,
    IReadOnlyList<ThemeColor> Colors);

public enum ThemeSource
{
    /// <summary>使用程序内置的默认主题（用户在 data\theme\color.txt 没有文件）。</summary>
    EmbeddedDefault,

    /// <summary>使用了 <exe同级>\data\theme\color.txt（用户文件）。</summary>
    UserFile,

    /// <summary>用户文件存在但解析失败，已回退内置默认。</summary>
    FallbackAfterError
}

public sealed record ThemeLoadResult(ThemePreset Preset, ThemeSource Source, string? Warning);

/// <summary>
/// 主题色服务。
///
/// 数据流：
///   1. 源码树里的 <c>Resources/color.txt</c> 作为**嵌入资源**编译进程序，是默认值来源；
///   2. 运行时优先读 <c>&lt;exe同级&gt;\data\theme\color.txt</c>（用户可改）；
///   3. 该文件不存在时，若数据目录可写，则把内置默认内容写出去（便于用户发现与修改）；
///   4. 任何解析失败一律回退内置默认，并在 <see cref="ThemeLoadResult.Warning"/> 中说明原因——
///      **绝不因为主题文件写错而影响启动**。
///
/// 编码：读写都走 <see cref="TextFileCodec"/>（写 UTF-8 带 BOM；读支持 BOM / UTF-8 / ANSI 回退）。
/// </summary>
public static class ThemeService
{
    public const string EmbeddedResourceName = "WhiteBoard.Core.Resources.color.txt";
    public const string ThemeFileName = "color.txt";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>读取嵌入的默认主题文本。</summary>
    public static string ReadEmbeddedJson()
    {
        var asm = typeof(ThemeService).Assembly;
        using var stream = asm.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"找不到嵌入资源 {EmbeddedResourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return TextFileCodec.Decode(ms.ToArray());
    }

    /// <summary>加载主题。永不抛异常（最坏情况回退内置默认）。</summary>
    public static ThemeLoadResult Load(PathService paths)
    {
        ThemePreset embedded;
        var embeddedJson = "";
        try
        {
            embeddedJson = ReadEmbeddedJson();
            embedded = Parse(embeddedJson);
        }
        catch (Exception ex)
        {
            // 内置资源都坏了说明构建有问题，给一个最小可用主题
            embedded = MinimalFallback();
            return new ThemeLoadResult(embedded, ThemeSource.FallbackAfterError,
                $"内置主题资源不可用：{ex.Message}");
        }

        var userFile = paths.ThemeFile;

        if (!File.Exists(userFile))
        {
            WriteDefaultIfPossible(paths, embeddedJson);
            return new ThemeLoadResult(embedded, ThemeSource.EmbeddedDefault, null);
        }

        try
        {
            var text = TextFileCodec.ReadAllText(userFile);
            var parsed = Parse(text);
            return new ThemeLoadResult(parsed, ThemeSource.UserFile, null);
        }
        catch (Exception ex)
        {
            return new ThemeLoadResult(embedded, ThemeSource.FallbackAfterError,
                $"主题文件解析失败，已回退内置默认。文件：{userFile}　原因：{ex.Message}");
        }
    }

    /// <summary>把内置默认写到用户主题文件（仅在不存在且可写时）。</summary>
    public static bool WriteDefaultIfPossible(PathService paths, string? defaultJson = null)
    {
        if (!paths.IsWritable) return false;
        try
        {
            var json = defaultJson ?? ReadEmbeddedJson();
            TextFileCodec.WriteAllText(paths.ThemeFile, json);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>解析主题 JSON，并做结构校验。</summary>
    public static ThemePreset Parse(string json)
    {
        var dto = JsonSerializer.Deserialize<ThemeFileDto>(json, JsonOptions)
                  ?? throw new FormatException("主题文件为空或不是合法 JSON");

        if (dto.Presets is null || dto.Presets.Count == 0)
            throw new FormatException("主题文件缺少 presets");

        var presets = new List<ThemePreset>();
        foreach (var p in dto.Presets)
        {
            if (p is null) continue;
            if (string.IsNullOrWhiteSpace(p.Id)) throw new FormatException("某个预设缺少 id");
            if (!ColorMath.IsValidHex(p.Background))
                throw new FormatException($"预设「{p.Name ?? p.Id}」的背景色非法：{p.Background}");
            if (p.Colors is null || p.Colors.Count == 0)
                throw new FormatException($"预设「{p.Name ?? p.Id}」没有颜色项");

            var colors = new List<ThemeColor>();
            foreach (var c in p.Colors)
            {
                if (c is null) continue;
                if (!ColorMath.IsValidHex(c.Value))
                    throw new FormatException($"颜色「{c.Name ?? c.Id}」的值非法：{c.Value}");
                colors.Add(new ThemeColor(
                    c.Id ?? $"color{colors.Count + 1}",
                    c.Name ?? c.Id ?? $"颜色{colors.Count + 1}",
                    Normalize(c.Value!)));
            }

            presets.Add(new ThemePreset(
                p.Id!, p.Name ?? p.Id!,
                string.IsNullOrWhiteSpace(p.BackgroundType) ? "solid" : p.BackgroundType!,
                Normalize(p.Background!),
                colors));
        }

        if (presets.Count == 0) throw new FormatException("presets 中没有有效项");

        // defaultPreset 找不到就取第一个（容错，不算错误）
        var chosen = presets.FirstOrDefault(x =>
                         string.Equals(x.Id, dto.DefaultPreset, StringComparison.OrdinalIgnoreCase))
                     ?? presets[0];

        return chosen;
    }

    private static string Normalize(string hex)
    {
        var (a, r, g, b) = ColorMath.ParseHex(hex);
        return a == 255
            ? $"#{r:X2}{g:X2}{b:X2}"
            : $"#{a:X2}{r:X2}{g:X2}{b:X2}";
    }

    private static ThemePreset MinimalFallback() => new(
        "fallback", "回退", "solid", "#2F4F3A",
        [
            new ThemeColor("white", "白笔", "#F5F5F0"),
            new ThemeColor("yellow", "黄笔", "#EED858"),
            new ThemeColor("red", "红笔", "#F47A6E"),
            new ThemeColor("blue", "蓝笔", "#6E9FE0")
        ]);

    // ---- DTO（字段名与 color.txt 一致） ----
    private sealed class ThemeFileDto
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("defaultPreset")] public string? DefaultPreset { get; set; }
        [JsonPropertyName("presets")] public List<PresetDto?>? Presets { get; set; }
    }

    private sealed class PresetDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("backgroundType")] public string? BackgroundType { get; set; }
        [JsonPropertyName("background")] public string? Background { get; set; }
        [JsonPropertyName("colors")] public List<ColorDto?>? Colors { get; set; }
    }

    private sealed class ColorDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("value")] public string? Value { get; set; }
    }
}
