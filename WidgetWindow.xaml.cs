using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace BatteryWidget;

public partial class WidgetWindow : Window
{
    private readonly WidgetSettings settings;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer deviceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer glassTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer glassDebounce = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly Dictionary<string, DeviceSnapshot> devices = new();
    private readonly HashSet<string> lowNotified = new();
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private bool reading, rerun, quitting, firstRead = true;
    private DeviceSnapshot[] visibleDevices = [];
    private DateTimeOffset? lastScan;
    private ResizeGesture? resize;
    private SettingsWindow? settingsWindow;

    private sealed record DeviceSnapshot(DeviceReading Reading, int? LastPercent = null, DateTimeOffset? ReadAt = null,
        IReadOnlyList<BatteryPart>? LastParts = null);
    private sealed record PartRow(string Text, Brush Accent);
    private sealed record DeviceRow(string Id, string Name, string Subtitle, int? Percent, string AccessibleLevel,
        Brush Accent, string Icon, string Tooltip, PartRow[] Parts)
    {
        public Visibility RingVisibility => Percent.HasValue || Parts.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private sealed record ResizeGesture(string Edges, Point Cursor, Rect Bounds, DpiScale Dpi);
    private static readonly Brush Muted = (Brush)new BrushConverter().ConvertFromString("#A7B4C9")!;
    private static readonly Brush Charging = (Brush)new BrushConverter().ConvertFromString("#79BCFF")!;
    private static readonly Brush Low = (Brush)new BrushConverter().ConvertFromString("#FFA396")!;
    private static readonly Brush Medium = (Brush)new BrushConverter().ConvertFromString("#FFD18B")!;
    private static readonly Brush Normal = (Brush)new BrushConverter().ConvertFromString("#91E4C6")!;

    internal WidgetWindow(WidgetSettings settings, bool preview = false)
    {
        this.settings = settings;
        InitializeComponent();
        Topmost = settings.AlwaysOnTop;
        MinWidth = WidgetSettings.MinimumWidth;
        MinHeight = WidgetSettings.MinimumHeight;
        MaxWidth = WidgetSettings.MaximumWidth;
        MaxHeight = WidgetSettings.MaximumHeight;
        Width = settings.Width;
        if (settings.Height is double savedHeight)
            SetManualHeight(savedHeight);
        Opacity = settings.OpacityPercent / 100.0;
        if (preview)
            return;
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
        Loaded += (_, _) =>
        {
            RestorePosition();
            ScheduleGlass();
            _ = RefreshAsync();
        };
        LocationChanged += (_, _) => ScheduleGlass();
        SizeChanged += (_, _) => ScheduleGlass();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                ScheduleGlass();
            else
                glassDebounce.Stop();
        };
        glassDebounce.Tick += (_, _) =>
        {
            glassDebounce.Stop();
            RefreshGlass();
        };
        glassTimer.Tick += (_, _) => RefreshGlass();
        glassTimer.Start();
        refreshTimer.Interval = TimeSpan.FromSeconds(settings.RefreshSeconds);
        refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        deviceTimer.Tick += (_, _) =>
        {
            deviceTimer.Stop();
            _ = RefreshAsync();
        };
        refreshTimer.Start();
        Closing += HandleClosing;
        CreateTray();
        foreach (var saved in settings.Devices.Where(entry => entry.Value.Pinned && entry.Value.LastName is not null))
        {
            var preference = saved.Value;
            devices[saved.Key] = new(new(saved.Key, preference.LastName!, preference.LastKind,
                preference.LastTransport ?? "蓝牙", LinkState.Disconnected));
        }
        ApplyReadings([DeviceReader.Mouse, DeviceReader.Keyboard], notify: false);
        StatusText.Text = "正在查找设备…";
    }

    private void RestorePosition()
    {
        Rect work = SystemParameters.WorkArea;
        Rect virtualDesktop = new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (settings.Left is double left && settings.Top is double top &&
            virtualDesktop.Contains(new Point(left + Width / 2, top + 30)))
        {
            Left = left;
            Top = top;
            return;
        }
        Left = work.Right - Width - 18;
        Top = work.Bottom - ActualHeight - 18;
    }

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr parameter, IntPtr data, ref bool handled)
    {
        const int deviceChange = 0x219, powerBroadcast = 0x218;
        if ((message == deviceChange && parameter.ToInt64() == 7)
            || (message == powerBroadcast && parameter.ToInt64() is 7 or 18))
        {
            deviceTimer.Stop();
            deviceTimer.Start();
        }
        return IntPtr.Zero;
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 收起桌面卡片", null, (_, _) => Dispatcher.InvokeAsync(ToggleVisibility));
        menu.Items.Add("刷新电量", null, (_, _) => _ = Dispatcher.InvokeAsync(RefreshAsync));
        menu.Items.Add("设置…", null, (_, _) => Dispatcher.InvokeAsync(() => { ShowCard(); OpenSettings(); }));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.InvokeAsync(Quit));
        string executable = Path.Combine(AppContext.BaseDirectory, "BatteryWidget.exe");
        if (File.Exists(executable))
            trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(executable);
        tray = new() { Text = "外设电量", Icon = trayIcon ?? System.Drawing.SystemIcons.Information,
            ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                _ = Dispatcher.InvokeAsync(ToggleVisibility);
        };
    }

    internal void ShowCard()
    {
        Show();
        if (settingsWindow is not null)
            settingsWindow.Activate();
        else
            Activate();
    }

    private void ToggleVisibility()
    {
        if (settingsWindow is not null)
        {
            settingsWindow.Activate();
            return;
        }
        if (IsVisible)
            Hide();
        else
            ShowCard();
    }

    private async Task RefreshAsync()
    {
        if (reading)
        {
            rerun = true;
            return;
        }
        reading = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在刷新…";
        try
        {
            var results = await Task.Run(() => DeviceReader.ReadAsync());
            if (quitting)
                return;
            lastScan = DateTimeOffset.Now;
            ApplyReadings(results, notify: true);
            if (firstRead && settings.Top is null)
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RestorePosition));
            firstRead = false;
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine(error);
            StatusText.Text = "刷新失败，可点击刷新重试";
        }
        finally
        {
            reading = false;
            RefreshButton.IsEnabled = true;
            if (rerun && !quitting)
            {
                rerun = false;
                _ = RefreshAsync();
            }
        }
    }

    private void ApplyReadings(DeviceReading[] results, bool notify)
    {
        var updates = results.ToDictionary(device => device.Id);
        foreach (var snapshot in devices.Values.ToArray())
        {
            var previous = snapshot.Reading;
            if (!updates.ContainsKey(previous.Id))
                updates.Add(previous.Id, previous with
                {
                    State = LinkState.Disconnected, Charging = null, Percent = null, Detail = null, Parts = null
                });
        }

        foreach (var update in updates.Values)
        {
            devices.TryGetValue(update.Id, out var previous);
            bool bluetoothCached = update.Transport == "蓝牙" && update.Detail is not null;
            bool hasParts = update.Parts is { Count: > 0 };
            DateTimeOffset? readAt = update.Percent.HasValue || hasParts
                ? bluetoothCached ? null : lastScan : previous?.ReadAt;
            devices[update.Id] = new(update, update.Percent ?? previous?.LastPercent, readAt,
                hasParts ? update.Parts : previous?.LastParts);
            if (settings.Devices.TryGetValue(update.Id, out var preference) && (preference.Hidden || preference.Pinned))
                RememberDevice(preference, update);
            if (notify)
                NotifyLow(update);
        }
        UpdateRows();
    }

    private void NotifyLow(DeviceReading reading)
    {
        if (reading.State != LinkState.Connected)
            return;
        var levels = new List<(string? Part, int Percent, bool? Charging)>();
        if (reading.Percent is int percent)
            levels.Add((null, percent, reading.Charging));
        if (settings.ShowEarbudParts && reading.Parts is not null)
            levels.AddRange(reading.Parts.Select(part => ((string?)part.Label, part.Percent, part.Charging)));
        if (levels.Count == 0)
            return;
        if (levels.All(level => level.Percent > settings.Threshold))
        {
            lowNotified.Remove(reading.Id);
            return;
        }
        var low = levels.Where(level => level.Percent <= settings.Threshold && level.Charging != true)
            .OrderBy(level => level.Percent).Take(1).ToArray();
        if (low.Length != 0 && settings.Notify && !settings.For(reading.Id).Hidden
            && tray is not null && lowNotified.Add(reading.Id))
        {
            var warning = low[0];
            string label = DisplayName(reading.Id);
            if (warning.Part is not null)
                label += $" · {warning.Part}";
            tray.ShowBalloonTip(5000, "外设电量偏低", $"{label} 剩余 {warning.Percent}%",
                Forms.ToolTipIcon.Warning);
        }
    }

    private void UpdateRows()
    {
        visibleDevices = devices.Values.Where(device =>
        {
            var preference = settings.For(device.Reading.Id);
            return !preference.Hidden && (preference.Pinned || device.Reading.State != LinkState.Disconnected);
        }).OrderBy(device => settings.For(device.Reading.Id).Order)
          .ThenBy(device => device.Reading.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        DeviceList.ItemsSource = visibleDevices.Select(MakeRow).ToArray();
        int online = visibleDevices.Count(device => device.Reading.State is LinkState.Connected or LinkState.Unavailable);
        EmptyText.Text = settings.Devices.Values.Any(preference => preference.Hidden)
            ? "右键卡片可重新添加设备" : "暂无可显示的设备";
        EmptyText.Visibility = visibleDevices.Length == 0 && lastScan.HasValue ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = lastScan.HasValue ? $"更新于 {lastScan.Value:HH:mm:ss}" : "正在查找设备…";
        if (tray is not null)
            tray.Text = "外设电量 · " + online.ToString(CultureInfo.CurrentCulture) + " 台在线";
    }

    private DeviceRow MakeRow(DeviceSnapshot snapshot)
    {
        var reading = snapshot.Reading;
        bool isCurrent = reading.Percent.HasValue && reading.State == LinkState.Connected;
        bool partsCurrent = reading.Parts is { Count: > 0 } && reading.State == LinkState.Connected;
        int? percent = reading.Percent ?? (partsCurrent ? null : snapshot.LastPercent);
        string state = reading.State switch
        {
            LinkState.Connected when reading.Charging == true => "充电中",
            LinkState.Connected when reading.Detail is not null => reading.Detail,
            LinkState.Connected when reading.Percent <= settings.Threshold => "低电量",
            LinkState.Connected => "已连接",
            LinkState.ReceiverOnly => "接收器已连接",
            LinkState.Unavailable => "电量不可用",
            LinkState.Unknown => reading.Detail ?? "连接状态未知",
            _ => "未连接"
        };
        string subtitle = $"{reading.Transport}  ·  {state}";
        if ((snapshot.LastPercent.HasValue && !reading.Percent.HasValue && !partsCurrent)
            || (snapshot.LastParts is { Count: > 0 } && !partsCurrent && !reading.Percent.HasValue))
            subtitle = $"{reading.Transport}  ·  上次电量" + (snapshot.ReadAt is DateTimeOffset at ? $"  {at:HH:mm}" : "");
        string icon = reading.Kind switch
        {
            DeviceKind.Mouse => "M7,2 L11,2 C14,2 16,5 16,8 L16,14 C16,17 14,20 9,20 C4,20 2,17 2,14 L2,8 C2,5 4,2 7,2 Z M9,3 L9,8",
            DeviceKind.Keyboard => "M1,4 L21,4 21,16 1,16 Z M5,8 H6 M10,8 H11 M15,8 H16 M5,12 H6 M9,12 H16",
            DeviceKind.Audio => "M2,13 V10 C2,0 18,0 18,10 V13 M2,11 H5 V18 H2 Z M18,11 H15 V18 H18 Z",
            _ => "M3,3 H17 V17 H3 Z M7,7 H13 V13 H7 Z"
        };
        string name = DisplayName(reading.Id);
        string tooltip = $"{name}\n{reading.Transport} · {state}";
        if (snapshot.ReadAt is DateTimeOffset time)
            tooltip += $"\n{(isCurrent || partsCurrent ? "电量读取时间" : "上次读取时间")}  {time:MM-dd HH:mm:ss}";
        if (reading.Detail is not null && reading.Detail != state)
            tooltip += "\n" + reading.Detail;
        if (name != reading.Name)
            tooltip += "\n" + reading.Name;
        var parts = reading.Parts is { Count: > 0 } ? reading.Parts : snapshot.LastParts;
        PartRow[] partRows = settings.ShowEarbudParts && parts is not null
            ? parts.Select(part => new PartRow($"{part.Label} {part.Percent}%",
                AccentFor(part.Percent, part.Charging, partsCurrent))).ToArray()
            : [];
        return new(reading.Id, name, subtitle, percent, percent is int value ? $"电量 {value}%" : "电量暂无数据",
            AccentFor(percent, reading.Charging, isCurrent), icon, tooltip, partRows);
    }

    private Brush AccentFor(int? percent, bool? charging, bool current) => !current || !percent.HasValue ? Muted
        : charging == true ? Charging : percent <= settings.Threshold ? Low : percent <= 40 ? Medium : Normal;

    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (settings.PositionLocked || e.LeftButton != MouseButtonState.Pressed)
            return;
        DragMove();
        SaveSettings();
    }

    private void GlassSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            GlassSurface.Clip = new RectangleGeometry(new Rect(e.NewSize), 28, 28);
    }

    private void ScheduleGlass()
    {
        if (!IsVisible || resize is not null)
            return;
        glassDebounce.Stop();
        glassDebounce.Start();
    }

    private void RefreshGlass()
    {
        if (!IsVisible || resize is not null || !IsLoaded)
            return;
        var origin = GlassSurface.PointToScreen(new Point());
        var end = GlassSurface.PointToScreen(new Point(GlassSurface.ActualWidth, GlassSurface.ActualHeight));
        var region = new Int32Rect((int)origin.X, (int)origin.Y, (int)Math.Round(end.X - origin.X), (int)Math.Round(end.Y - origin.Y));
        Backdrop.Source = WidgetBackdrop.Capture(new WindowInteropHelper(this).Handle, region);
    }

    private void SetManualHeight(double height)
    {
        Height = height;
        SizeToContent = SizeToContent.Manual;
        DeviceScroll.MaxHeight = double.PositiveInfinity;
    }

    private static Point ScreenCursor()
    {
        var cursor = Forms.Control.MousePosition;
        return new Point(cursor.X, cursor.Y);
    }

    private void ResizeStarted(object sender, DragStartedEventArgs e)
    {
        string edges = (string)((Thumb)sender).Tag;
        resize = new(edges, ScreenCursor(), new Rect(Left, Top, ActualWidth, ActualHeight), VisualTreeHelper.GetDpi(this));
        if (edges.Contains('T') || edges.Contains('B'))
            SetManualHeight(ActualHeight);
    }

    private void ResizeMoved(object sender, DragDeltaEventArgs e)
    {
        if (resize is not { } gesture)
            return;
        Vector change = ScreenCursor() - gesture.Cursor;
        double dx = change.X / gesture.Dpi.DpiScaleX;
        double dy = change.Y / gesture.Dpi.DpiScaleY;
        var bounds = gesture.Bounds;
        if (gesture.Edges.Contains('L'))
        {
            Width = Math.Clamp(bounds.Width - dx, MinWidth, MaxWidth);
            Left = bounds.Right - Width;
        }
        if (gesture.Edges.Contains('R'))
            Width = Math.Clamp(bounds.Width + dx, MinWidth, MaxWidth);
        if (gesture.Edges.Contains('T'))
        {
            Height = Math.Clamp(bounds.Height - dy, MinHeight, MaxHeight);
            Top = bounds.Bottom - Height;
        }
        if (gesture.Edges.Contains('B'))
            Height = Math.Clamp(bounds.Height + dy, MinHeight, MaxHeight);
    }

    private void ResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        resize = null;
        ScheduleGlass();
        SaveSettings();
    }

    private void RefreshClick(object sender, RoutedEventArgs e) => _ = RefreshAsync();
    private void HideClick(object sender, RoutedEventArgs e) => Hide();

    private void MenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        ShowCardMenu(menu);
    }

    private void CardMenuClick(object sender, MouseButtonEventArgs e)
    {
        ShowCardMenu(new ContextMenu { Placement = PlacementMode.MousePoint });
        e.Handled = true;
    }

    private void ShowCardMenu(ContextMenu menu)
    {
        menu.Items.Add(MenuAction("设置…", OpenSettings));
        menu.Items.Add(AddDeviceMenu());
        menu.Items.Add(MenuAction("刷新电量", () => _ = RefreshAsync()));
        menu.Items.Add(MenuAction("收起到托盘", Hide));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuAction("退出", Quit));
        menu.IsOpen = true;
    }

    private MenuItem AddDeviceMenu()
    {
        var menu = new MenuItem { Header = "添加设备" };
        foreach (string id in settings.Devices.Where(entry => entry.Value.Hidden).Select(entry => entry.Key)
            .OrderBy(DisplayName, StringComparer.CurrentCulture))
            menu.Items.Add(MenuAction(DisplayName(id), () =>
            {
                SetDeviceVisibility(id, visible: true);
                UpdateRows();
                SaveSettings();
            }));
        if (menu.Items.Count == 0)
            menu.Items.Add(new MenuItem { Header = "暂无移除的设备", IsEnabled = false });
        return menu;
    }

    private string DisplayName(string id)
    {
        var preference = settings.For(id);
        return preference.Alias ?? (id == DeviceReader.MouseId ? "ROG Harpe Ace · Aim Lab"
            : devices.GetValueOrDefault(id)?.Reading.Name ?? preference.LastName ?? "蓝牙设备");
    }

    private static void RememberDevice(DevicePreference preference, DeviceReading reading)
    {
        preference.LastName = reading.Name;
        preference.LastKind = reading.Kind;
        preference.LastTransport = reading.Transport;
    }

    private void SetDeviceVisibility(string id, bool visible)
    {
        var preference = settings.For(id);
        if (preference.Hidden != visible)
            return;
        preference.Hidden = !visible;
        if (devices.TryGetValue(id, out var snapshot))
        {
            RememberDevice(preference, snapshot.Reading);
            if (visible && snapshot.Reading.State == LinkState.Disconnected)
                preference.Pinned = true;
        }
        else if (visible)
        {
            preference.Pinned = true;
            devices[id] = new(new(id, preference.LastName ?? preference.Alias ?? "蓝牙设备", preference.LastKind,
                preference.LastTransport ?? "蓝牙", LinkState.Disconnected));
        }
    }

    private void OpenSettings()
    {
        if (settingsWindow is not null)
        {
            settingsWindow.Activate();
            return;
        }
        bool startup = WidgetSettings.StartsWithWindows();
        var options = devices.Keys.Concat(settings.Devices.Where(entry => entry.Value.Hidden).Select(entry => entry.Key))
            .Distinct().OrderBy(id => settings.For(id).Order).ThenBy(DisplayName, StringComparer.CurrentCulture)
            .Select(id => new SettingsWindow.DeviceOption(id, DisplayName(id), !settings.For(id).Hidden)).ToArray();
        var dialog = new SettingsWindow(settings, startup, options, value => Opacity = value / 100.0) { Owner = this };
        settingsWindow = dialog;
        try
        {
            if (dialog.ShowDialog() == true && dialog.Result is { } changes)
                ApplySettings(changes, startup);
        }
        finally
        {
            settingsWindow = null;
            Opacity = settings.OpacityPercent / 100.0;
        }
    }

    private void ApplySettings(SettingsWindow.Changes changes, bool previousStartup)
    {
        settings.OpacityPercent = changes.Opacity;
        Width = changes.Width;
        if (changes.ResetHeight)
        {
            SizeToContent = SizeToContent.Height;
            Height = double.NaN;
            DeviceScroll.MaxHeight = 490;
        }
        settings.RefreshSeconds = changes.RefreshSeconds;
        refreshTimer.Interval = TimeSpan.FromSeconds(changes.RefreshSeconds);
        Topmost = settings.AlwaysOnTop = changes.Topmost;
        settings.PositionLocked = changes.LockPosition;
        settings.Notify = changes.Notify;
        settings.Threshold = changes.Threshold;
        settings.ShowEarbudParts = changes.EarbudParts;
        foreach (var option in changes.Devices)
            SetDeviceVisibility(option.Id, option.Visible);
        UpdateRows();
        SaveSettings();
        if (previousStartup != changes.Startup)
        {
            try { WidgetSettings.SetStartsWithWindows(changes.Startup); }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { StatusText.Text = "无法更改开机启动设置"; }
        }
        ScheduleGlass();
    }

    private void DeviceMenuClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not DeviceRow row)
            return;
        var preference = settings.For(row.Id);
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(MenuAction("重命名", () => EditName(row.Id, row.Name)));
        menu.Items.Add(CheckItem("断开后仍显示", preference.Pinned, value =>
        {
            preference.Pinned = value;
            if (devices.TryGetValue(row.Id, out var snapshot))
                RememberDevice(preference, snapshot.Reading);
            UpdateRows();
        }));
        int index = Array.FindIndex(visibleDevices, device => device.Reading.Id == row.Id);
        menu.Items.Add(MenuAction("上移", () => MoveRow(row.Id, -1), index > 0));
        menu.Items.Add(MenuAction("下移", () => MoveRow(row.Id, 1), index >= 0 && index + 1 < visibleDevices.Length));
        menu.Items.Add(MenuAction("从卡片移除", () =>
        {
            SetDeviceVisibility(row.Id, visible: false);
            UpdateRows();
            SaveSettings();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(AddDeviceMenu());
        menu.Items.Add(MenuAction("设置…", OpenSettings));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void EditName(string id, string current)
    {
        var field = new TextBox { Text = current, MaxLength = 60, Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(5) };
        var save = new Button { Content = "保存", IsDefault = true, MinWidth = 72, Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(field);
        panel.Children.Add(save);
        var dialog = new Window { Title = "重命名设备", Owner = this, Width = 300, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Content = panel };
        save.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => { field.Focus(); field.SelectAll(); };
        if (dialog.ShowDialog() != true)
            return;
        settings.For(id).Alias = string.IsNullOrWhiteSpace(field.Text) ? null : field.Text.Trim();
        UpdateRows();
        SaveSettings();
    }

    private void MoveRow(string id, int direction)
    {
        int oldIndex = Array.FindIndex(visibleDevices, device => device.Reading.Id == id);
        int newIndex = oldIndex + direction;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= visibleDevices.Length)
            return;
        (visibleDevices[oldIndex], visibleDevices[newIndex]) = (visibleDevices[newIndex], visibleDevices[oldIndex]);
        for (int i = 0; i < visibleDevices.Length; i++)
            settings.For(visibleDevices[i].Reading.Id).Order = i;
        UpdateRows();
        SaveSettings();
    }

    private MenuItem CheckItem(string title, bool value, Action<bool> change)
    {
        var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = value };
        item.Click += (_, _) => { change(item.IsChecked); SaveSettings(); };
        return item;
    }

    private static MenuItem MenuAction(string title, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = title, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private void SaveSettings()
    {
        if (double.IsFinite(Left) && double.IsFinite(Top))
        {
            settings.Left = Left;
            settings.Top = Top;
        }
        settings.Width = Width;
        settings.Height = SizeToContent == SizeToContent.Manual && double.IsFinite(Height) ? Height : null;
        if (!settings.Save())
            StatusText.Text = "无法保存设置";
    }

    private void HandleClosing(object? sender, CancelEventArgs e)
    {
        if (!quitting)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void Quit()
    {
        quitting = true;
        settingsWindow?.Close();
        SaveSettings();
        refreshTimer.Stop();
        deviceTimer.Stop();
        glassTimer.Stop();
        glassDebounce.Stop();
        if (tray is not null)
        {
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        }
        trayIcon?.Dispose();
        Close();
    }

    internal void SavePreview(string path, DeviceReading[] readings)
    {
        lastScan = DateTimeOffset.Now;
        ApplyReadings(readings, notify: false);
        Panel.Measure(new Size(Width, settings.Height ?? double.PositiveInfinity));
        int height = (int)Math.Ceiling(settings.Height ?? Math.Max(MinHeight, Panel.DesiredSize.Height));
        Panel.Arrange(new Rect(0, 0, Width, height));
        Panel.UpdateLayout();
        var image = new RenderTargetBitmap((int)Width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(Panel);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
