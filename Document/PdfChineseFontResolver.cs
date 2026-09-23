using PdfSharp.Fonts;

namespace ScreenTranslator.Document;

/// <summary>Supplies a bundled Simplified Chinese font so PDF export works offline on clean Windows installs.</summary>
public sealed class PdfChineseFontResolver : IFontResolver
{
    private const string FaceName = "ScreenTranslator-NotoSansSC";
    private readonly string _fontPath;
    private readonly Lazy<byte[]> _fontBytes;

    private PdfChineseFontResolver(string fontPath)
    {
        _fontPath = fontPath;
        _fontBytes = new Lazy<byte[]>(() =>
        {
            if (!File.Exists(_fontPath))
                throw new FileNotFoundException("PDF 导出所需的内置中文字体不存在。请修复或重新安装 ScreenTranslator。", _fontPath);
            return File.ReadAllBytes(_fontPath);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static void Configure()
    {
        if (GlobalFontSettings.FontResolver is not null) return;
        var fontPath = Path.Combine(AppContext.BaseDirectory, "ModelsData", "Fonts", "NotoSansSC-VF.ttf");
        GlobalFontSettings.FontResolver = new PdfChineseFontResolver(fontPath);
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(FaceName, mustSimulateBold: bold, mustSimulateItalic: italic);

    public byte[]? GetFont(string faceName) => faceName == FaceName ? _fontBytes.Value : null;
}
