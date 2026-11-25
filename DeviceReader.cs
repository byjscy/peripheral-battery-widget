using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static BatteryWidget.NativeDevices;

namespace BatteryWidget;

internal enum DeviceKind { Mouse, Keyboard, Audio, Other }
internal enum LinkState { Connected, Unavailable, ReceiverOnly, Unknown, Disconnected }
internal enum BatteryPartKind { LeftEarbud, RightEarbud, ChargingCase }
internal sealed record BatteryPart(BatteryPartKind Kind, int Percent, bool? Charging = null)
{
    internal string Label => Kind switch
    {
        BatteryPartKind.LeftEarbud => "左耳",
        BatteryPartKind.RightEarbud => "右耳",
        _ => "充电仓"
    };
}

internal sealed record DeviceReading(string Id, string Name, DeviceKind Kind, string Transport, LinkState State,
    int? Percent = null, bool? Charging = null, string? Detail = null, IReadOnlyList<BatteryPart>? Parts = null);

internal static class DeviceReader
{
    internal const string MouseId = "harpe-ace";
    internal const string KeyboardId = "vgn-v87";
    internal static readonly DeviceReading Mouse = new(MouseId, "ROG Harpe Ace Aim Lab Edition", DeviceKind.Mouse,
        "2.4G", LinkState.Disconnected);
    internal static readonly DeviceReading Keyboard = new(KeyboardId, "VGN V87 Pro", DeviceKind.Keyboard,
        "2.4G", LinkState.Disconnected);

    internal static async Task<DeviceReading[]> ReadAsync(Action<string>? diagnostic = null)
    {
        var interfaces = HidInterfaces();
        var mouseTask = ReadMouseAsync(interfaces);
        var keyboardTask = ReadKeyboardAsync(interfaces);
        var bluetoothTask = Task.Run(() => BluetoothReader.Read(diagnostic));
        await Task.WhenAll(mouseTask, keyboardTask, bluetoothTask).ConfigureAwait(false);
        return new[] { mouseTask.Result, keyboardTask.Result }.Concat(bluetoothTask.Result)
            .GroupBy(device => device.Id)
            .Select(group => group.OrderBy(device => device.State).First())
            .ToArray();
    }

    internal static bool IsHarpe(ushort productId) => productId is 0x1A92 or 0x1A94;

    private static async Task<DeviceReading> ReadMouseAsync(HidInterface[] interfaces)
    {
        var result = Mouse;
        foreach (var device in interfaces.Where(device => device.Vendor == 0x0B05 && IsHarpe(device.ProductId)
            && IsReportInterface(device, 0xFF00, 1, 65)))
        {
            result = await ReadMouseBatteryAsync(device, result with
            {
                Transport = device.ProductId == 0x1A92 ? "USB" : "2.4G",
                State = LinkState.Unavailable
            }, 0).ConfigureAwait(false);
            if (result.Percent.HasValue)
                return result;
        }

        var omni = interfaces.Where(device => device.Vendor == 0x0B05 && device.ProductId == 0x1ACE).ToArray();
        foreach (var info in omni.Where(device => IsReportInterface(device, 0xFF02, 1, 64)
            && device.InterfaceName.Contains("&MI_02", StringComparison.OrdinalIgnoreCase)))
        {
            var pair = await QueryAsync(info, [1, 0xA0], response => response[0] == 1 && response[1] == 0xA0)
                .ConfigureAwait(false);
            if (pair is null && result.State == LinkState.Disconnected)
                result = Mouse with { State = LinkState.ReceiverOnly, Detail = "接收器未返回配对信息" };
            if (pair is null || !HasHarpePair(pair))
                continue;
            result = Mouse with { State = LinkState.ReceiverOnly, Detail = "唤醒鼠标后刷新" };
            var endpoint = omni.FirstOrDefault(device => device.PairGroup == info.PairGroup
                && device.InterfaceName.Contains("&MI_02", StringComparison.OrdinalIgnoreCase)
                && device.Caps.Usage is 1 or 3 && IsReportInterface(device, 0xFF01, device.Caps.Usage, 64));
            if (endpoint is null)
                continue;
            result = await ReadMouseBatteryAsync(endpoint, result, 3).ConfigureAwait(false);
            if (result.Percent.HasValue)
                return result;
        }
        return result;
    }

    private static bool HasHarpePair(byte[] reply)
    {
        for (int index = 5; index + 3 < reply.Length; index += 4)
        {
            ushort productId = (ushort)(reply[index] | (reply[index + 1] << 8));
            if (productId == 0)
                break;
            if (IsHarpe(productId))
                return true;
        }
        return false;
    }

    private static async Task<DeviceReading> ReadMouseBatteryAsync(HidInterface device, DeviceReading fallback, byte report)
    {
        var reply = await QueryAsync(device, [report, 0x12, 7],
            bytes => bytes[0] == report && bytes[1] == 0x12 && bytes[2] == 7 && bytes[3] == 0).ConfigureAwait(false);
        if (reply is null || reply[5] > 100 || (reply[5] == 0 && reply[10] == 0))
            return fallback;
        return fallback with { State = LinkState.Connected, Percent = reply[5], Charging = reply[10] != 0, Detail = null };
    }

    private static async Task<DeviceReading> ReadKeyboardAsync(HidInterface[] interfaces)
    {
        var result = Keyboard;
        var candidates = interfaces.Where(device => device.Vendor == 0x320F && device.ProductId is 0x5088 or 0x5055
            && IsReportInterface(device, 0xFF1C, 0x92, 64)
            && (device.Product.StartsWith("VXE V87 ", StringComparison.OrdinalIgnoreCase)
                || device.Product.StartsWith("VGN V87 ", StringComparison.OrdinalIgnoreCase)
                || device.Product.Equals("VXE V87", StringComparison.OrdinalIgnoreCase)
                || device.Product.Equals("VGN V87", StringComparison.OrdinalIgnoreCase)
                || device.Product.StartsWith("VGN V87PRO", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(device => device.ProductId); // Prefer the cable when both are connected.
        foreach (var device in candidates)
        {
            bool wireless = device.ProductId == 0x5088;
            result = Keyboard with { Transport = wireless ? "2.4G" : "USB",
                State = wireless ? LinkState.ReceiverOnly : LinkState.Unavailable,
                Detail = wireless ? "唤醒键盘后刷新" : null };
            var reply = await QueryAsync(device, [4, 0x20, 0, 0x1A, 6], bytes =>
                bytes[0] == 4 && bytes[3] == 0x1A && bytes[4] == 6
                && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0 && bytes[8] <= 100 && bytes[9] <= 2)
                .ConfigureAwait(false);
            if (reply is not null)
                return result with { State = LinkState.Connected, Percent = reply[8], Charging = reply[9] != 0, Detail = null };
        }
        return result;
    }

    private static bool IsReportInterface(HidInterface device, ushort page, ushort usage, ushort size) =>
        device.Caps.Page == page && device.Caps.Usage == usage && device.Caps.Input == size && device.Caps.Output == size;
}
