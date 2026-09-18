using WhiteBoard.Core.Model;

namespace WhiteBoard.Core.Commands;

/// <summary>
/// 修改已有文本（内容 + 重新测量的尺寸）。
///
/// 为什么连尺寸一起改：文字改了之后外框必须跟着变，否则选中框、命中测试、
/// 视口裁剪都会用旧尺寸——表现是"选中框比文字小一圈""点了文字却选不中"。
/// </summary>
public sealed class SetTextCommand : CommandBase
{
    private readonly Page _page;
    private readonly TextObject _obj;
    private readonly string _newText;
    private readonly double _newWidth;
    private readonly double _newHeight;

    private string _oldText = "";
    private double _oldWidth;
    private double _oldHeight;

    public SetTextCommand(Page page, TextObject obj, string newText, double newWidth, double newHeight)
    {
        _page = page;
        _obj = obj;
        _newText = newText;
        _newWidth = newWidth;
        _newHeight = newHeight;
    }

    public override string Name => "修改文本";

    public Page Page => _page;
    public TextObject Target => _obj;

    protected override void Capture()
    {
        _oldText = _obj.Text;
        _oldWidth = _obj.LocalWidth;
        _oldHeight = _obj.LocalHeight;
    }

    protected override void Apply()
    {
        _obj.Text = _newText;
        _obj.LocalWidth = _newWidth;
        _obj.LocalHeight = _newHeight;
    }

    protected override void Revert()
    {
        _obj.Text = _oldText;
        _obj.LocalWidth = _oldWidth;
        _obj.LocalHeight = _oldHeight;
    }
}

/// <summary>修改文本的字体/字号（S1 用于"选中的文本换字号"）。</summary>
public sealed class SetTextStyleCommand : CommandBase
{
    private readonly TextObject _obj;
    private readonly double _newFontSize;
    private readonly bool _newBold;
    private readonly bool _newItalic;
    private readonly double _newWidth;
    private readonly double _newHeight;

    private double _oldFontSize;
    private bool _oldBold;
    private bool _oldItalic;
    private double _oldWidth;
    private double _oldHeight;

    public SetTextStyleCommand(TextObject obj, double newFontSize, bool bold, bool italic,
        double newWidth, double newHeight)
    {
        _obj = obj;
        _newFontSize = newFontSize;
        _newBold = bold;
        _newItalic = italic;
        _newWidth = newWidth;
        _newHeight = newHeight;
    }

    public override string Name => "调整文字样式";

    protected override void Capture()
    {
        _oldFontSize = _obj.FontSize;
        _oldBold = _obj.Bold;
        _oldItalic = _obj.Italic;
        _oldWidth = _obj.LocalWidth;
        _oldHeight = _obj.LocalHeight;
    }

    protected override void Apply()
    {
        _obj.FontSize = _newFontSize;
        _obj.Bold = _newBold;
        _obj.Italic = _newItalic;
        _obj.LocalWidth = _newWidth;
        _obj.LocalHeight = _newHeight;
    }

    protected override void Revert()
    {
        _obj.FontSize = _oldFontSize;
        _obj.Bold = _oldBold;
        _obj.Italic = _oldItalic;
        _obj.LocalWidth = _oldWidth;
        _obj.LocalHeight = _oldHeight;
    }
}
