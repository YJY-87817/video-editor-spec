using System.Windows;
using VideoEditor.Core.Logging;

namespace VideoEditor.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Initialize(new FileLog(FileLog.DefaultDirectory));
        Log.Info("程式啟動");
        // 其他執行緒或背景工作的例外不會經過 DispatcherUnhandledException，這裡至少留下紀錄。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error(args.IsTerminating ? "未處理的例外（程式即將結束）" : "未處理的例外", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("沒有人觀察的背景工作例外", args.Exception);
            args.SetObserved();
        };
        LibVLCSharp.Shared.Core.Initialize();
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("介面執行緒的未處理例外", args.Exception);
            // 對話框只給簡短說明：完整堆疊與路徑寫在紀錄檔，使用者貼出截圖時不會洩漏路徑。
            MessageBox.Show($"{args.Exception.Message}\n\n詳細資料已寫入紀錄：\n{Log.Directory}",
                "發生未預期的錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
