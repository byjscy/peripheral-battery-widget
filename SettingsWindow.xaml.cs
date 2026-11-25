using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BatteryWidget;

public partial class SettingsWindow : Window
{
    private readonly Action<int> previewOpacity;
    private bool syncingOpacity = true;

    internal sealed class DeviceOption(string id, string name, bool visible)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public bool Visible { get; set; } = visible;
    }

    internal sealed record Changes(int Opacity, int Width, bool ResetHeight, int RefreshSeconds, bool Topmost,
        bool LockPosition, bool Startup, bool Notify, int Threshold, bool EarbudParts, DeviceOption[] Devices);

    internal Changes? Result { get; private set; }

    internal SettingsWindow(WidgetSettings settings, bool startsWithWindows, DeviceOption[] devices, Action<int> preview)
    {
        previewOpacity = preview;
        InitializeComponent();
        OpacitySlider.Value = settings.OpacityPercent;
        OpacityInput.Text = settings.OpacityPercent.ToString(CultureInfo.InvariantCulture);
        WidthInput.Text = ((int)Math.Round(settings.Width)).ToString(CultureInfo.InvariantCulture);
        RefreshInput.Text = settings.RefreshSeconds.ToString(CultureInfo.InvariantCulture);
        TopmostCheck.IsChecked = settings.AlwaysOnTop;
        LockCheck.IsChecked = settings.PositionLocked;
        StartupCheck.IsChecked = startsWithWindows;
        NotifyCheck.IsChecked = settings.Notify;
        EarbudPartsCheck.IsChecked = settings.ShowEarbudParts;
        foreach (int value in WidgetSettings.ThresholdOptions)
            ThresholdInput.Items.Add(new ComboBoxItem { Content = $"{value}%", Tag = value });
        ThresholdInput.SelectedIndex = Array.IndexOf(WidgetSettings.ThresholdOptions, settings.Threshold);
        DeviceOptions.ItemsSource = devices;
        ResetHeightButton.IsEnabled = settings.Height.HasValue;
        syncingOpacity = false;
    }

    private void DragTitle(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void ResetHeightClick(object sender, RoutedEventArgs e)
    {
        ResetHeightButton.Tag = true;
        ResetHeightButton.IsEnabled = false;
    }

    private void OpacitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (syncingOpacity)
            return;
        int value = (int)Math.Round(e.NewValue);
        syncingOpacity = true;
        OpacityInput.Text = value.ToString(CultureInfo.InvariantCulture);
        syncingOpacity = false;
        previewOpacity(value);
    }

    private void OpacityInputChanged(object sender, TextChangedEventArgs e)
    {
        if (syncingOpacity || !TryNumber(OpacityInput, WidgetSettings.MinimumOpacity, WidgetSettings.MaximumOpacity, out int value))
            return;
        syncingOpacity = true;
        OpacitySlider.Value = value;
        syncingOpacity = false;
        previewOpacity(value);
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!TryNumber(OpacityInput, WidgetSettings.MinimumOpacity, WidgetSettings.MaximumOpacity, out int opacity))
        {
            ShowError("不透明度请输入 20–100。", OpacityInput);
            return;
        }
        if (!TryNumber(WidthInput, WidgetSettings.MinimumWidth, WidgetSettings.MaximumWidth, out int width))
        {
            ShowError("卡片宽度请输入 286–720。", WidthInput);
            return;
        }
        if (!TryNumber(RefreshInput, WidgetSettings.MinimumRefresh, WidgetSettings.MaximumRefresh, out int refresh))
        {
            ShowError("刷新间隔请输入 10–3600 秒。", RefreshInput);
            return;
        }
        int threshold = (ThresholdInput.SelectedItem as ComboBoxItem)?.Tag is int value ? value : 20;
        Result = new(opacity, width, ResetHeightButton.Tag is true, refresh, TopmostCheck.IsChecked == true,
            LockCheck.IsChecked == true, StartupCheck.IsChecked == true, NotifyCheck.IsChecked == true, threshold,
            EarbudPartsCheck.IsChecked == true, (DeviceOption[])DeviceOptions.ItemsSource);
        DialogResult = true;
    }

    private void ShowError(string text, TextBox field)
    {
        ErrorText.Text = text;
        field.Focus();
        field.SelectAll();
    }

    private static bool TryNumber(TextBox field, int min, int max, out int value) =>
        int.TryParse(field.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
}
