using System.Windows;

namespace ScanningPowerCylinder
{
    /// <summary>
    /// 应用程序入口。注：StartupUri 在 App.xaml 中未设置，
    /// 由本类在 OnStartup 中实例化主窗口，与 Qt 原工程 main.cpp 行为一致。
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var window = new MainWindow();
            window.Show();
        }
    }
}
