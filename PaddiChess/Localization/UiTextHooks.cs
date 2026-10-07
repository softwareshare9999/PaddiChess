using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;

namespace PaddiXiangqi.Localization;

/// <summary>
/// Translates text as it reaches the visual tree, so XAML, code-behind, bindings, templates,
/// tooltips and placeholders need no per-string changes. SetCurrentValue keeps bindings and
/// styles in place; the next upstream value is translated again. Board pieces are drawn with
/// FormattedText, not TextBlock, and are therefore never affected.
/// </summary>
internal static class UiTextHooks
{
    [ThreadStatic] private static bool _applying;
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        // Covers TextBlock, SelectableTextBlock and AccessText, which also render string Content,
        // Header, ToolTip and PlaceholderText through control templates.
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((target, e) =>
        {
            if (Apply(target, TextBlock.TextProperty, e.NewValue, exactOnly: false)) WidenFixedLabelColumn(target);
        });
        Run.TextProperty.Changed.AddClassHandler<Run>((target, e) => Apply(target, Run.TextProperty, e.NewValue, exactOnly: false));
        Window.TitleProperty.Changed.AddClassHandler<Window>((target, e) => Apply(target, Window.TitleProperty, e.NewValue, exactOnly: false));
        NativeMenuItem.HeaderProperty.Changed.AddClassHandler<NativeMenuItem>((target, e) => Apply(target, NativeMenuItem.HeaderProperty, e.NewValue, exactOnly: false));
        // Editable text belongs to the user; only whole default values (e.g. 未命名棋谱) are replaced.
        // Template parts (ComboBox, NumericUpDown) are two-way bound to their owner and must not be touched.
        TextBox.TextProperty.Changed.AddClassHandler<TextBox>((target, e) =>
        {
            if (target.TemplatedParent is null) Apply(target, TextBox.TextProperty, e.NewValue, exactOnly: !target.IsReadOnly);
        });
    }

    private static bool Apply(AvaloniaObject target, AvaloniaProperty property, object? value, bool exactOnly)
    {
        if (_applying || value is not string text || !TranslationCatalog.ContainsCjk(text)) return false;
        var translated = exactOnly ? L10n.TExact(text) : L10n.T(text);
        if (string.Equals(translated, text, StringComparison.Ordinal)) return false;
        _applying = true;
        try { target.SetCurrentValue(property, translated); }
        finally { _applying = false; }
        return true;
    }

    /// <summary>
    /// Labels sized in pixels for two Chinese characters (ColumnDefinitions="32,…") would clip the
    /// longer translation; such a column grows to fit, keeping its original width as the minimum.
    /// </summary>
    private static void WidenFixedLabelColumn(TextBlock label)
    {
        if (label.Parent is null)
        {
            label.AttachedToLogicalTree += OnAttached;
            return;
        }
        if (label.Parent is not Grid grid || Grid.GetColumnSpan(label) != 1) return;
        var index = Grid.GetColumn(label);
        if (index >= grid.ColumnDefinitions.Count) return;
        var column = grid.ColumnDefinitions[index];
        if (!column.Width.IsAbsolute) return;
        column.MinWidth = Math.Max(column.MinWidth, column.Width.Value);
        column.Width = GridLength.Auto;
    }

    private static void OnAttached(object? sender, Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        if (sender is not TextBlock label) return;
        label.AttachedToLogicalTree -= OnAttached;
        WidenFixedLabelColumn(label);
    }
}
