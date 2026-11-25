using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace BatteryWidget;

internal sealed class WidgetSettings
{
    internal const int MinimumWidth = 286, MaximumWidth = 720;
    internal const int MinimumHeight = 178, MaximumHeight = 760;
    internal const int MinimumOpacity = 20, MaximumOpacity = 100;
    internal const int MinimumRefresh = 10, MaximumRefresh = 3600;
    internal static readonly int[] ThresholdOptions = [10, 15, 20, 25, 30];
    private static readonly string SettingsFile = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private const string StartupPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName = "PeripheralBatteryWidget";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public bool AlwaysOnTop { get; set; }
    public bool PositionLocked { get; set; }
    public bool Notify { get; set; } = true;
    public int Threshold { get; set; } = 20;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 382;
    public double? Height { get; set; }
    public int OpacityPercent { get; set; } = 100;
    public int RefreshSeconds { get; set; } = 60;
    public bool ShowEarbudParts { get; set; } = true;
    public Dictionary<string, DevicePreference> Devices { get; set; } = new()
    {
        [DeviceReader.MouseId] = new() { Pinned = true, Order = 0 },
        [DeviceReader.KeyboardId] = new() { Pinned = true, Order = 1 }
    };

    internal DevicePreference For(string id)
    {
        if (!Devices.TryGetValue(id, out var device))
        {
            device = new();
            Devices.Add(id, device);
        }
        return device;
    }

    internal static WidgetSettings Load()
    {
        try
        {
            var settings = File.Exists(SettingsFile)
                ? JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(SettingsFile), JsonOptions) ?? new()
                : new();
            settings.Width = Math.Clamp(settings.Width, MinimumWidth, MaximumWidth);
            if (settings.Height is double height)
                settings.Height = Math.Clamp(height, MinimumHeight, MaximumHeight);
            settings.OpacityPercent = Math.Clamp(settings.OpacityPercent, MinimumOpacity, MaximumOpacity);
            settings.RefreshSeconds = Math.Clamp(settings.RefreshSeconds, MinimumRefresh, MaximumRefresh);
            if (!ThresholdOptions.Contains(settings.Threshold))
                settings.Threshold = 20;
            settings.Devices ??= new WidgetSettings().Devices;
            return settings;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    internal bool Save()
    {
        try
        {
            string temp = SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, SettingsFile, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupPath);
        return key?.GetValue(StartupName) is string registered
            && string.Equals(registered, StartupCommand(), StringComparison.OrdinalIgnoreCase);
    }

    internal static void SetStartsWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupPath);
        if (enabled)
            key.SetValue(StartupName, StartupCommand());
        else
            key.DeleteValue(StartupName, throwOnMissingValue: false);
    }

    private static string StartupCommand() => $"\"{Path.Combine(AppContext.BaseDirectory, "BatteryWidget.exe")}\"";
}

internal sealed class DevicePreference
{
    public string? Alias { get; set; }
    public bool Pinned { get; set; }
    public bool Hidden { get; set; }
    public int Order { get; set; } = 1000;
    public string? LastName { get; set; }
    public DeviceKind LastKind { get; set; } = DeviceKind.Other;
    public string? LastTransport { get; set; }
}
