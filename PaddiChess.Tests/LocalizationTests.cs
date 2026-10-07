using System.Text.Json;
using System.Text.RegularExpressions;
using PaddiXiangqi.Localization;

namespace PaddiXiangqi.Tests;

public sealed class LocalizationTests
{
    private static readonly Lazy<TranslationCatalog> Vietnamese = new(() => new TranslationCatalog(OpenCatalog()));

    private static Stream OpenCatalog() =>
        typeof(L10n).Assembly.GetManifestResourceStream("PaddiXiangqi.Localization.vi.json")
        ?? throw new InvalidOperationException("Vietnamese catalog is not embedded.");

    [Fact]
    public void CatalogIsEmbeddedAndPopulated() => Assert.True(Vietnamese.Value.Count > 900);

    [Theory]
    [InlineData("对弈", "Chơi cờ")]
    [InlineData("① 连接并同步", "① Kết nối và đồng bộ")]
    [InlineData("已同步 12 手 · 等待红方走棋", "Đã đồng bộ 12 nước · chờ Đỏ đi")]
    [InlineData("红胜 40% · 和 20% · 黑胜 40%", "Đỏ thắng 40% · hòa 20% · Đen thắng 40%")]
    [InlineData("读取记录失败：棋谱文件为空。", "Đọc nhật ký thất bại: Tệp kỳ phổ trống.")]
    [InlineData("截图暂不可用，保留连接并自动重试（2） · 无法启动截图服务", "Tạm thời không chụp được màn hình, giữ kết nối và tự động thử lại (2) · Không thể khởi động dịch vụ chụp màn hình")]
    public void TranslatesExactTemplateAndComposedText(string source, string expected) =>
        Assert.Equal(expected, Vietnamese.Value.Translate(source));

    [Theory]
    [InlineData("炮二平五", "P2-5")]
    [InlineData("馬八进七", "M8.7")]
    [InlineData("前車进一", "Xt.1")]
    [InlineData("帥五退一", "Tg5/1")]
    [InlineData("1. 炮二平五  ·  馬八进七", "1. P2-5  ·  M8.7")]
    public void ConvertsMoveNotation(string source, string expected) =>
        Assert.Equal(expected, Vietnamese.Value.Translate(source));

    [Fact]
    public void NamesPiecesInProse() =>
        Assert.Equal("Đã chọn Xe Đỏ · nhấp vào vị trí đích để di chuyển", Vietnamese.Value.Translate("已选中红俥 · 点击目标位置移动"));

    [Fact]
    public void TranslatesMultiLineSummariesAssembledFromFragments()
    {
        var translated = Vietnamese.Value.Translate(
            "内置皮卡鱼：4 线程 · 128 MB · 2 路 · 最长 2 秒 · 深度上限 13\n分析模式：3 路候选。调整参数后从下一次搜索生效。");
        Assert.False(TranslationCatalog.ContainsCjk(translated), translated);
        Assert.EndsWith("\nChế độ phân tích: 3 biến ứng viên. Thay đổi tham số có hiệu lực từ lần tìm kiếm tiếp theo.", translated);
    }

    [Theory]
    [InlineData("")]
    [InlineData("rnbakabnr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RNBAKABNR w - - 0 1")]
    [InlineData("Pikafish 2026-09-25")]
    public void LeavesTextWithoutChineseUnchanged(string text) => Assert.Same(text, Vietnamese.Value.Translate(text));

    [Fact]
    public void ExactModeOnlyReplacesWholeEntries()
    {
        Assert.Equal("Kỳ phổ chưa đặt tên", Vietnamese.Value.TranslateExact("未命名棋谱"));
        Assert.Equal("我的未命名棋谱", Vietnamese.Value.TranslateExact("我的未命名棋谱"));
    }

    [Fact]
    public void TranslationsKeepEveryPlaceholder()
    {
        using var document = JsonDocument.Parse(OpenCatalog());
        var placeholder = new Regex(@"\{\d+\}");
        var broken = document.RootElement.GetProperty("strings").EnumerateObject()
            .Where(entry => entry.Value.GetString() is { Length: > 0 } target &&
                !placeholder.Matches(entry.Name).Select(m => m.Value).Order().SequenceEqual(placeholder.Matches(target).Select(m => m.Value).Order()))
            .Select(entry => entry.Name)
            .ToList();
        Assert.Empty(broken);
    }

    [Fact]
    public void SourceLanguageDisablesTranslation()
    {
        Assert.Equal("zh", L10n.RequestedLanguage(["--lang", "zh"]));
        Assert.Equal("vi", L10n.RequestedLanguage(["--lang=vi"]));
    }
}
