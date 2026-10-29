using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Acose.CustomerManager.UI;

namespace Acose.CustomerManager;

public partial class App : Application
{
    private Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--website-import-self-test"))
        {
            Shutdown(WebsiteImportTestRunner.Run(e.Args)); return;
        }
        try
        {
            string baseDirectory = AppContext.BaseDirectory;
            string dataDirectory = Path.Combine(baseDirectory, "Data");
            if (e.Args.Contains("--configure-from-environment"))
            {
                EnvironmentVault.Capture(dataDirectory);
                Shutdown(0); return;
            }
            bool preview = e.Args.Contains("--preview");
            string mutexName = "Local\\AcoseCustomerManager-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseDirectory.ToUpperInvariant())))[..24];
            _mutex = new Mutex(true, mutexName + (preview ? "-preview" : ""), out bool acquired);
            if (!acquired)
            {
                MessageBox.Show("Trình quản lý đang mở. Hãy chọn cửa sổ ACOSE trên thanh tác vụ.", "ACOSE", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(); return;
            }
            if (!preview) EnvironmentVault.Apply(dataDirectory);
            var controller = new ManagerController(dataDirectory,
                Path.Combine(baseDirectory, "Engine", "AutomationPlatform.Presentation.exe"), preview);
            var window = new MainWindow(controller);
            if (preview) window.Title = "ACOSE · Bản xem trước — dữ liệu minh họa";
            MainWindow = window;
            int screenshotIndex = Array.IndexOf(e.Args, "--screenshot");
            if (preview && screenshotIndex >= 0 && screenshotIndex + 1 < e.Args.Length)
            {
                string output = Path.GetFullPath(e.Args[screenshotIndex + 1]);
                window.ContentRendered += async (_, _) =>
                {
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var content = (FrameworkElement)window.Content;
                    var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    using (var stream = File.Create(output)) encoder.Save(stream);
                    if (e.Args.Contains("--exit-after-screenshot")) window.Close();
                };
            }
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Không mở được ACOSE", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}
