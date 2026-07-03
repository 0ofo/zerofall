using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;

namespace ZeroFall.SqlEditor;

/// <summary>AvaloniaEdit 超链接默认纯蓝，暗色背景下可读性差；按主题覆写链接色。</summary>
internal static class AvaloniaEditEditorHelper
{
    private static readonly Color DarkLink = Color.Parse("#9CDCFE");
    private static readonly Color LightLink = Color.Parse("#0969DA");

    public static void ApplyTheme(TextEditor? editor)
    {
        if (editor?.TextArea?.TextView is not { } view)
            return;

        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        view.LinkTextForegroundBrush = new SolidColorBrush(isDark ? DarkLink : LightLink);
        view.LinkTextUnderline = false;
    }
}
