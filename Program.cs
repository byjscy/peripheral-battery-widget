using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace BatteryWidget;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--diagnose", var report])
        {
            var log = new ConcurrentQueue<string>();
            var result = DeviceReader.ReadAsync(log.Enqueue).GetAwaiter().GetResult();
            File.WriteAllLines(report, log.Concat(result.Select(device =>
                $"{device.Name} | {device.Transport} | {device.State} | " +
                (device.Percent.HasValue ? $"{device.Percent}%" : "—") +
                (device.Charging == true ? " | 充电中" : "") + (device.Detail is null ? "" : $" | {device.Detail}") +
                (device.Parts is { Count: > 0 } ? " | " + string.Join(" / ", device.Parts.Select(part => $"{part.Label} {part.Percent}%")) : ""))));
            return 0;
        }
        if (args is ["--preview", var preview])
        {
            var result = DeviceReader.ReadAsync().GetAwaiter().GetResult();
            _ = new Application();
            new WidgetWindow(new(), preview: true).SavePreview(preview, result);
            return 0;
        }

        const string instanceName = @"Local\PeripheralBatteryWidget.D8B8146F";
        using var instance = new Mutex(true, instanceName, out bool first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Show");
        if (!first)
        {
            show.Set();
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new WidgetWindow(WidgetSettings.Load());
        var wakeHandler = ThreadPool.RegisterWaitForSingleObject(show,
            (_, _) => window.Dispatcher.InvokeAsync(window.ShowCard), null, Timeout.Infinite, executeOnlyOnce: false);
        try
        {
            return app.Run(window);
        }
        finally
        {
            wakeHandler.Unregister(null);
            instance.ReleaseMutex();
        }
    }
}
