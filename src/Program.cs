using SysWidge.Config;
using SysWidge.Ui;

namespace SysWidge;

internal static class Program
{
    // Unique per-app GUID so a second launch just exits instead of stacking widgets.
    private const string SingleInstanceMutex = "SysWidge_SingleInstance_{b6a1f2e0-7c3d-4a51-9f2a-2d8e3c1f4a7b}";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out bool isNew);
        if (!isNew)
            return;

        // HighDpiMode comes from the csproj (PerMonitorV2); this also enables
        // visual styles and sets compatible text rendering.
        ApplicationConfiguration.Initialize();

        var config = WidgetConfig.Load();
        Application.Run(new WidgetForm(config));

        GC.KeepAlive(mutex);
    }
}
