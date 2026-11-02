using System.Windows;

namespace VideoEditor.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        LibVLCSharp.Shared.Core.Initialize();
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), "發生未預期的錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
