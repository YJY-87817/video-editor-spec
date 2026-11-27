using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Ffmpeg;
using VideoEditor.Core.Logging;
using VideoEditor.Core.Models;
using VideoEditor.Core.Project;

namespace VideoEditor.App;

public partial class MainWindow
{
    // ---- 專案存檔、開啟與自動恢復（UIUX-PLAN-2 階段 6） ----

    private const string ProjectFilter = "影片編輯器專案|*" + ProjectFile.Extension;

    private readonly AutoSaveTracker _autoSave = new();
    private DispatcherTimer? _autoSaveTimer;
    private MediaProbe? _probe;
    // 目前的專案檔；還沒存過時是 null。
    private string? _projectPath;
    // 存檔（或開啟、開新專案）時的狀態；和現在不同就是有沒存的變更。null 表示一定有（例如從自動存檔還原）。
    private ProjectState? _savedState;

    private ProjectState CurrentState() =>
        new(_timeline.Capture(), SelectedRatio, SelectedMode, [.. _media.Select(m => m.Info.Path)]);

    private bool IsDirty => _savedState == null || !_savedState.Equals(CurrentState());

    /// <summary>建構式最後呼叫：目前（空白）的狀態算已存。自動存檔的計時要等還原的詢問處理完才開始（<see cref="StartAutoSave"/>）。</summary>
    private void StartProjectTracking(MediaProbe probe)
    {
        _probe = probe;
        MarkSaved();
    }

    /// <summary>
    /// 開始每秒檢查一次是否要自動存檔、更新標題。必須在 <see cref="OfferAutoSaveRecoveryAsync"/> 之後：
    /// 剛開啟時沒有沒存的變更，計時器會把上次留下的自動存檔當成不需要而刪掉，還原就找不到檔案了。
    /// </summary>
    private void StartAutoSave()
    {
        if (_autoSaveTimer != null || _probe == null) return;
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoSaveTimer.Tick += (_, _) => OnAutoSaveTick();
        _autoSaveTimer.Start();
    }

    private void MarkSaved()
    {
        _savedState = CurrentState();
        _autoSave.Reset(_savedState);
        UpdateTitle();
    }

    /// <summary>視窗標題：專案名稱，有沒存的變更時加上 *。</summary>
    private void UpdateTitle()
    {
        var name = _projectPath == null ? "未命名專案" : Path.GetFileNameWithoutExtension(_projectPath);
        Title = $"{name}{(IsDirty ? " *" : "")} — 影片編輯器";
    }

    /// <summary>
    /// 每秒一次：更新標題；有沒存的變更、而且 3 秒沒有再改時寫自動存檔（不覆蓋使用者的專案檔）；
    /// 沒有沒存的變更時刪掉自動存檔，下次開啟才不會問要不要還原。
    /// </summary>
    private void OnAutoSaveTick()
    {
        UpdateTitle();
        if (!IsDirty)
        {
            DeleteAutoSave();
            return;
        }
        var state = CurrentState();
        if (!_autoSave.Observe(state)) return;
        try
        {
            var path = AutoSaveTracker.DefaultPath;
            ProjectSerializer.Save(ProjectSerializer.Create(_timeline, _media.Select(m => m.Info), SelectedRatio, SelectedMode, path, _projectPath), path);
            _autoSave.MarkSaved(state);
        }
        catch (Exception e) when (ExpectedErrors.IsFile(e))
        {
            Log.Warn("自動存檔失敗", e);
            _autoSave.MarkSaved(state); // 不要每秒重試、洗掉紀錄；下次修改再試
        }
    }

    private static void DeleteAutoSave()
    {
        try
        {
            if (File.Exists(AutoSaveTracker.DefaultPath)) File.Delete(AutoSaveTracker.DefaultPath);
        }
        catch (Exception e) when (ExpectedErrors.IsFile(e))
        {
            Log.Warn("刪除自動存檔失敗", e);
        }
    }

    /// <summary>啟動時發現上次的自動存檔（上次沒有正常關閉）：詢問是否還原。不還原就刪掉。</summary>
    private async Task OfferAutoSaveRecoveryAsync()
    {
        if (!File.Exists(AutoSaveTracker.DefaultPath)) return;
        var answer = MessageBox.Show(this, "上次的剪輯沒有存檔就關閉了（可能是程式當掉或電腦關機）。\n\n要還原嗎？", "還原上次的剪輯",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Yes) await LoadProjectAsync(AutoSaveTracker.DefaultPath, fromAutoSave: true);
        else DeleteAutoSave();
    }

    // ---- 檔案選單與快捷鍵 ----

    private void OnFileMenuClick(object sender, RoutedEventArgs e)
    {
        FileMenu.PlacementTarget = FileMenuButton;
        FileMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        FileMenu.IsOpen = true;
    }

    private void OnNewProjectClick(object sender, RoutedEventArgs e) => NewProject();
    private void OnOpenProjectClick(object sender, RoutedEventArgs e) => RunOpenProject();

    /// <summary>選單與 Ctrl+O 共用：開啟不等結果，預料之外的錯誤要記錄並顯示，不能默默丟掉或讓程式當掉。</summary>
    private async void RunOpenProject()
    {
        try
        {
            await OpenProjectAsync(null);
        }
        catch (Exception e)
        {
            Log.Error("開啟專案時發生預料之外的錯誤", e);
            ShowStatus($"開啟專案時發生錯誤：{e.Message}", StatusKind.Error);
        }
    }

    // 正在開啟專案（讀取素材資訊要好幾秒）：這段期間不能再開啟、開新或存檔。
    private bool _loadingProject;

    private bool RejectWhileLoading()
    {
        if (!_loadingProject) return false;
        ShowStatus("正在開啟專案，請等它完成。", StatusKind.Warning);
        return true;
    }
    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveProject(saveAs: false);
    private void OnSaveAsClick(object sender, RoutedEventArgs e) => SaveProject(saveAs: true);

    /// <summary>Ctrl+N／Ctrl+O／Ctrl+S／Ctrl+Shift+S。回傳 true 表示已處理。</summary>
    private bool HandleFileKey(KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        switch (ShortcutKey(e))
        {
            case Key.N when modifiers == ModifierKeys.Control: NewProject(); break;
            case Key.O when modifiers == ModifierKeys.Control: RunOpenProject(); break;
            case Key.S when modifiers == ModifierKeys.Control: SaveProject(saveAs: false); break;
            case Key.S when modifiers == (ModifierKeys.Control | ModifierKeys.Shift): SaveProject(saveAs: true); break;
            default: return false;
        }
        e.Handled = true;
        return true;
    }

    /// <summary>
    /// 有沒存的變更時詢問「要儲存變更嗎？」：儲存（存檔成功才繼續）／不儲存／取消。回傳是否可以繼續（關閉、開新、開啟別的）。
    /// </summary>
    private bool ConfirmSaveChanges()
    {
        if (_tools == null || !IsDirty) return true;
        var answer = MessageBox.Show(this, "目前的剪輯有沒存的變更。\n\n要儲存變更嗎？", "儲存變更",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);
        return answer switch
        {
            MessageBoxResult.Yes => SaveProject(saveAs: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    /// <summary>存檔；還沒有專案檔或「另存新檔」時先選位置。回傳是否存好了（取消或失敗是 false）。</summary>
    private bool SaveProject(bool saveAs)
    {
        if (_tools == null || RejectWhileLoading()) return false;
        var path = _projectPath;
        if (saveAs || path == null)
        {
            var first = _timeline.VideoClips.FirstOrDefault()?.Media.Path ?? _media.FirstOrDefault()?.Info.Path;
            var dialog = new SaveFileDialog
            {
                Filter = ProjectFilter,
                DefaultExt = ProjectFile.Extension,
                FileName = _projectPath != null ? Path.GetFileName(_projectPath)
                    : first != null ? Path.GetFileNameWithoutExtension(first) + ProjectFile.Extension : "未命名專案" + ProjectFile.Extension,
                InitialDirectory = ExistingFolder(_settings.LastProjectFolder) ?? ExistingFolder(first == null ? null : Path.GetDirectoryName(first)) ?? "",
            };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }
        try
        {
            ProjectSerializer.Save(ProjectSerializer.Create(_timeline, _media.Select(m => m.Info), SelectedRatio, SelectedMode, path), path);
        }
        catch (Exception e) when (ExpectedErrors.IsFile(e))
        {
            Log.Warn($"儲存專案失敗：{path}", e);
            MessageBox.Show(this, $"無法儲存專案：\n{e.Message}", "儲存失敗", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        _projectPath = path;
        _settings = _settings with { LastProjectFolder = Path.GetDirectoryName(path) };
        MarkSaved();
        DeleteAutoSave();
        ShowStatus($"已儲存專案：{path}", StatusKind.Success);
        return true;
    }

    private void NewProject()
    {
        if (_tools == null || RejectWhileLoading() || !ConfirmSaveChanges()) return;
        _engine?.Pause();
        _projectPath = null;
        ReplaceContents([], timeline => timeline.Restore(new TimelineSnapshot([], [])));
        MarkSaved();
        DeleteAutoSave();
        ShowStatus("已開新專案");
    }

    private async Task OpenProjectAsync(string? path)
    {
        if (_tools == null || RejectWhileLoading() || !ConfirmSaveChanges()) return;
        if (path == null)
        {
            var dialog = new OpenFileDialog { Filter = ProjectFilter, InitialDirectory = ExistingFolder(_settings.LastProjectFolder) ?? "" };
            if (dialog.ShowDialog(this) != true) return;
            path = dialog.FileName;
        }
        await LoadProjectAsync(path, fromAutoSave: false);
    }

    /// <summary>
    /// 開啟專案：讀檔與檢查 → 找素材（找不到的讓使用者重新指定或略過）→ 重新讀取素材資訊 → 換掉素材清單與時間軸。
    /// 從自動存檔還原時，專案檔路徑用自動存檔裡記的原本位置，而且算有沒存的變更。
    /// </summary>
    private async Task LoadProjectAsync(string path, bool fromAutoSave)
    {
        if (_probe is not { } probe || RejectWhileLoading()) return;
        _loadingProject = true;
        try
        {
            await LoadProjectCoreAsync(path, fromAutoSave, probe);
        }
        finally
        {
            _loadingProject = false;
        }
    }

    private async Task LoadProjectCoreAsync(string path, bool fromAutoSave, MediaProbe probe)
    {
        var loaded = ProjectSerializer.Load(path);
        if (loaded.File is not { } project)
        {
            MessageBox.Show(this, $"無法開啟專案：\n{loaded.Error}", "開啟失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 相對路徑以原本的專案檔為準（自動存檔放在程式自己的資料夾，相對路徑沒有意義）。
        var relativeBase = fromAutoSave ? project.SourceProjectPath ?? path : path;
        var paths = project.Media.Select(m => ProjectSerializer.ResolveMediaPath(m, relativeBase, File.Exists)).ToList();
        if (paths.Any(p => p == null) && !AskForMissingMedia(project, paths)) return;

        var infos = new List<MediaInfo?>();
        var problems = new List<string>();
        _engine?.Pause();
        // 讀取期間畫面還能操作；讀完要換掉內容前，確認這段期間沒有改過東西，改過就問。
        var stateBeforeLoading = CurrentState();
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                ProgressPanel.ShowImport($"開啟專案：讀取素材 {i + 1}/{paths.Count}", (double)i / paths.Count);
                if (paths[i] is not { } file)
                {
                    infos.Add(null);
                    continue;
                }
                try
                {
                    infos.Add(await probe.ProbeAsync(file));
                }
                catch (Exception e) when (ExpectedErrors.IsProbe(e))
                {
                    Log.Warn($"開啟專案時讀不到素材：{file}", e is FfmpegException ? null : e);
                    infos.Add(null);
                    problems.Add($"{Path.GetFileName(file)}：讀不到素材資訊，略過。");
                }
            }
        }
        finally
        {
            ProgressPanel.HideImport();
        }

        var items = new List<MediaItem>();
        foreach (var info in infos.OfType<MediaInfo>().Distinct()) items.Add(await CreateMediaItemAsync(info));
        if (!stateBeforeLoading.Equals(CurrentState())
            && MessageBox.Show(this, "開啟專案期間，目前的剪輯有了修改。\n\n要放棄這些修改，繼續開啟專案嗎？", "開啟專案",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            ShowStatus("已取消開啟專案。", StatusKind.Warning);
            return;
        }
        IReadOnlyList<string> buildProblems = [];
        ReplaceContents(items, timeline => buildProblems = ProjectSerializer.Build(project, infos, timeline));
        problems.AddRange(buildProblems);
        RatioButton(project.Ratio).IsChecked = true;
        (project.Mode == FitMode.Pad ? ModePad : ModeFill).IsChecked = true;

        _projectPath = fromAutoSave ? project.SourceProjectPath : path;
        if (fromAutoSave)
        {
            _savedState = null;
            _autoSave.Reset(CurrentState());
            UpdateTitle();
        }
        else
        {
            _settings = _settings with { LastProjectFolder = Path.GetDirectoryName(path) };
            MarkSaved();
        }
        if (_timeline.AllClips.Any()) SwitchToTimelinePreview();

        if (problems.Count > 0)
        {
            var shown = string.Join('\n', problems.Take(10)) + (problems.Count > 10 ? $"\n…還有 {problems.Count - 10} 項" : "");
            MessageBox.Show(this, $"專案已開啟，但有些內容沒辦法載入：\n\n{shown}", "開啟專案", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ShowStatus(fromAutoSave ? "已還原上次沒有存檔的剪輯" : $"已開啟專案：{Path.GetFileName(path)}", StatusKind.Success);
    }

    /// <summary>換掉素材清單與時間軸（開新專案、開啟專案）：清掉選取、入出點、復原紀錄，重新整理畫面。</summary>
    private void ReplaceContents(IReadOnlyList<MediaItem> items, Action<Timeline> fillTimeline)
    {
        TimelineView.Select(null);
        _points.Clear();
        _media.Clear();
        foreach (var item in items)
        {
            _media.Add(item);
            _waveforms?.Request(item.Info);
        }
        fillTimeline(_timeline);
        _history.Clear();
        OnClipsChanged();
        MediaList.SelectedItem = _media.FirstOrDefault();
        EmptyHint.Visibility = _media.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>素材清單的項目，縮圖在背景產生（失敗時沒有縮圖）。</summary>
    private async Task<MediaItem> CreateMediaItemAsync(MediaInfo info)
    {
        byte[]? thumbnail = null;
        if (info.HasVideo && _extractor != null)
        {
            try
            {
                thumbnail = await _extractor.ThumbnailAsync(info.Path, info.IsStill ? 0 : Math.Min(1, info.Duration / 10), 144, default, info.IsStill);
            }
            catch (Exception e) when (ExpectedErrors.IsProcess(e))
            {
                // 沒有縮圖不影響其他功能。
            }
        }
        return new MediaItem(info) { Thumbnail = thumbnail is { Length: > 0 } ? TryCreateThumbnail(thumbnail) : null };
    }

    /// <summary>
    /// 找不到的素材：列出來，每個可以「重新指定…」或「略過」（決定 3）。回傳 false 表示取消開啟。
    /// <paramref name="paths"/> 會被改成使用者指定的位置（略過的維持 null）。
    /// </summary>
    private bool AskForMissingMedia(ProjectFile project, List<string?> paths)
    {
        var dim = (Brush)new BrushConverter().ConvertFromString("#9A9A9A")!;
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        for (var i = 0; i < paths.Count; i++)
        {
            if (paths[i] != null) continue;
            var index = i;
            var status = new TextBlock { Text = "找不到", Foreground = Palette.StatusWarning, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            var relocate = new Button { Content = "重新指定…", Margin = new Thickness(0, 0, 6, 0) };
            var skip = new Button { Content = "略過", Margin = new Thickness(0) };
            relocate.Click += (_, _) =>
            {
                var dialog = new OpenFileDialog { Title = $"找 {Path.GetFileName(project.Media[index].Path)}", FileName = Path.GetFileName(project.Media[index].Path) };
                if (dialog.ShowDialog() != true) return;
                paths[index] = dialog.FileName;
                status.Text = $"→ {dialog.FileName}";
                status.Foreground = Palette.StatusSuccess;
            };
            skip.Click += (_, _) =>
            {
                paths[index] = null;
                status.Text = "略過（用到它的片段不會載入）";
                status.Foreground = dim;
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            buttons.Children.Add(relocate);
            buttons.Children.Add(skip);
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = Path.GetFileName(project.Media[index].Path), Foreground = Brushes.Gainsboro, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = project.Media[index].Path, Foreground = dim, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(status);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(text);
            list.Children.Add(row);
        }

        var proceed = false;
        var ok = new Button { Content = "繼續開啟", IsDefault = true, Padding = new Thickness(14, 5, 14, 5) };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(ok);
        footer.Children.Add(cancel);
        var content = new DockPanel { Margin = new Thickness(20, 14, 20, 16) };
        var header = new TextBlock { Text = "下列素材找不到。可以重新指定位置，或略過（用到它們的片段不會載入）。", Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(header);
        content.Children.Add(footer);
        content.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var window = new Window
        {
            Title = "找不到素材",
            Owner = this,
            Content = content,
            Width = 620,
            Height = Math.Min(560, 180 + 70 * paths.Count(p => p == null)),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)new BrushConverter().ConvertFromString("#1E1E1E")!,
            FontFamily = FontFamily,
        };
        ok.Click += (_, _) =>
        {
            proceed = true;
            window.Close();
        };
        window.ShowDialog();
        return proceed;
    }
}
