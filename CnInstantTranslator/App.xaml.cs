using CnInstantTranslator.Config;
using CnInstantTranslator.Core;
using CnInstantTranslator.UI;

namespace CnInstantTranslator;

public partial class App : System.Windows.Application
{
    private AppController? _controller;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        AppSettings settings = SettingsStore.Load();
        var overlay = new TranslationOverlay(settings);
        _controller = new AppController(settings, overlay);
        _controller.Start();
        overlay.Show();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _controller?.Dispose();
        base.OnExit(e);
    }
}
