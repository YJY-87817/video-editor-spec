using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoEditor.Core.Playback;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 快捷鍵一覽（F1 或時間軸工具列的「?」） ----

    private void OnShortcutsClick(object sender, RoutedEventArgs e) => ShowShortcuts();

    /// <summary>內容來自 <see cref="Shortcuts.All"/>，和快捷鍵的定義放在一起，不會和實際行為對不上。Esc、F1 或 Enter 關閉。</summary>
    private void ShowShortcuts()
    {
        var grid = new Grid { Margin = new Thickness(20, 12, 20, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        string? group = null;
        foreach (var shortcut in Shortcuts.All)
        {
            if (shortcut.Group != group)
            {
                group = shortcut.Group;
                AddRow(grid, new TextBlock
                {
                    Text = group, Foreground = (Brush)new BrushConverter().ConvertFromString("#9CC4F0")!, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, grid.RowDefinitions.Count == 0 ? 0 : 12, 0, 4),
                }, null);
            }
            AddRow(grid,
                new TextBlock { Text = shortcut.Keys, FontFamily = new FontFamily("Consolas, Microsoft JhengHei UI"), Foreground = Palette.Accent, Margin = new Thickness(0, 2, 24, 2) },
                new TextBlock { Text = shortcut.Description, Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
        }

        var window = new Window
        {
            Title = "快捷鍵一覽",
            Owner = this,
            Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Width = 560,
            SizeToContent = SizeToContent.Height,
            MaxHeight = Math.Max(300, ActualHeight - 80),
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)new BrushConverter().ConvertFromString("#1E1E1E")!,
            FontFamily = FontFamily,
        };
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.F1 or Key.Enter) window.Close();
        };
        window.ShowDialog();
    }

    private static void AddRow(Grid grid, TextBlock first, TextBlock? second)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(first, row);
        if (second == null) Grid.SetColumnSpan(first, 2);
        grid.Children.Add(first);
        if (second == null) return;
        Grid.SetRow(second, row);
        Grid.SetColumn(second, 1);
        grid.Children.Add(second);
    }
}
