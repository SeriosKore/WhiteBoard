using System.Text;

namespace WhiteBoard.Core.Text;

/// <summary>
/// 文本文件编码编解码。
/// 约定（总路线 §5.1 红线 4）：
///   写入 —— 统一 UTF-8（带 BOM，Windows 记事本兼容性最好）；
///   读取 —— 先剥离 BOM → 优先 UTF-8 严格解码 → 失败回退系统 ANSI（中文即 GBK）。
/// 这样用户用记事本"另存为 ANSI"后程序仍能正确读出中文，而不是整体回退默认值。
/// </summary>
public static class TextFileCodec
{
    private static readonly UTF8Encoding Utf8StrictNoBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly UTF8Encoding Utf8WithBom = new(
        encoderShouldEmitUTF8Identifier: true,
        throwOnInvalidBytes: false);

    private static bool _codePagesRegistered;

    /// <summary>读出文本。BOM 优先，其次 UTF-8，最后系统 ANSI。</summary>
    public static string ReadAllText(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>从字节解码。见类注释的优先级。</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        // 1) 剥离 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Utf8StrictNoBom.GetString(bytes[3..]);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);

        // 2) 优先 UTF-8 严格解码（用户侧首选）
        try
        {
            return Utf8StrictNoBom.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 不是合法 UTF-8，落到 ANSI
        }

        // 3) 回退系统 ANSI（zh-CN 下为 GBK/936）
        return GetSystemAnsi().GetString(bytes);
    }

    /// <summary>写入文本：UTF-8 带 BOM。</summary>
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllBytes(path, Encode(content));
    }

    /// <summary>编码为 UTF-8 带 BOM 的字节。</summary>
    public static byte[] Encode(string content)
    {
        var preamble = Utf8WithBom.GetPreamble();
        var body = Utf8WithBom.GetBytes(content);
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    /// <summary>
    /// 取系统 ANSI 代码页编码。
    /// 注意：.NET Core 起 <c>Encoding.Default</c> 是 UTF-8 而非 ANSI，
    /// 中文系统的 ANSI（GBK/936）必须通过 CodePagesEncodingProvider 注册后取得。
    /// </summary>
    public static Encoding GetSystemAnsi()
    {
        EnsureCodePagesRegistered();
        try
        {
            var cp = Thread.CurrentThread.CurrentCulture.TextInfo.ANSICodePage;
            return Encoding.GetEncoding(cp);
        }
        catch (Exception)
        {
            // 注册表/区域信息异常时退回 936（简体中文 GBK），再不行退回 Latin1（不会抛）
            try { return Encoding.GetEncoding(936); }
            catch (Exception) { return Encoding.Latin1; }
        }
    }

    private static void EnsureCodePagesRegistered()
    {
        if (_codePagesRegistered) return;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch (Exception)
        {
            // 没有该提供程序时保持现状（GetSystemAnsi 会走兜底路径）
        }
        _codePagesRegistered = true;
    }
}
