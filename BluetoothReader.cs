using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using static BatteryWidget.NativeDevices;

namespace BatteryWidget;

internal static class BluetoothReader
{
    private static readonly Guid BatteryService = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryCharacteristic = new("00002a19-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryStatusCharacteristic = new("00002bed-0000-1000-8000-00805f9b34fb");
    private static readonly PropertyKey BatteryProperty = new(new("104ea319-6ee2-4701-bd47-8ddbf425bbe5"), 2);
    private static readonly PropertyKey ContainerId = new(new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    private static readonly PropertyKey ConnectedProperty = new(ContainerProperties, 55);
    private static readonly Regex DeviceAddress = new(@"dev_([0-9a-f]{12})(?![0-9a-f])", RegexOptions.IgnoreCase);
    private const uint ForceDevice = 4, ForceCache = 8;

    private sealed record NodeInfo(string? Address, Guid? Container, bool IsRoot, bool IsClassicRoot,
        string? Name, bool? Connected, int? Battery);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        private ushort year, month, dayOfWeek, day, hour, minute, second, millisecond;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ClassicDevice
    {
        public uint Size;
        public ulong Address;
        private uint deviceClass;
        public int Connected;
        private int remembered, authenticated;
        private SystemTime lastSeen, lastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] private string name;
    }

    [DllImport("bthprops.cpl", ExactSpelling = true)]
    private static extern uint BluetoothGetDeviceInfo(IntPtr radio, ref ClassicDevice device);

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct BluetoothUuid
    {
        [FieldOffset(0)] public byte IsShort;
        [FieldOffset(4)] public ushort Short;
        [FieldOffset(4)] public Guid Long;
        public readonly bool Matches(ushort number, Guid uuid) => IsShort != 0 ? Short == number : Long == uuid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Characteristic
    {
        private ushort serviceHandle;
        public BluetoothUuid Uuid;
        private ushort attributeHandle, valueHandle;
        private byte broadcastable;
        public byte Readable;
        private byte writable, writeWithoutResponse, signedWritable, notifiable, indicatable, extended;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Descriptor
    {
        private ushort serviceHandle, characteristicHandle;
        public int Type;
        private BluetoothUuid uuid;
        private ushort attributeHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DescriptorValue
    {
        private int type;
        private BluetoothUuid uuid;
        private byte format, exponent;
        private BluetoothUuid unit;
        public byte Namespace;
        public BluetoothUuid Description;
        public uint DataSize;
    }

    private sealed record GattReading(int? Total, IReadOnlyList<BatteryPart> Parts, bool? Charging);
    private sealed record GattValue(int Percent, BatteryPartKind? Part, bool? Charging);

    [DllImport("BluetoothApis.dll", ExactSpelling = true)]
    private static extern int BluetoothGATTGetCharacteristics(SafeFileHandle service, IntPtr parent, ushort capacity,
        [Out] Characteristic[]? characteristics, out ushort count, uint flags);

    [DllImport("BluetoothApis.dll", ExactSpelling = true)]
    private static extern int BluetoothGATTGetCharacteristicValue(SafeFileHandle service, in Characteristic characteristic,
        uint capacity, [Out] byte[] value, out ushort size, uint flags);

    [DllImport("BluetoothApis.dll", ExactSpelling = true)]
    private static extern int BluetoothGATTGetDescriptors(SafeFileHandle service, in Characteristic characteristic,
        ushort capacity, [Out] Descriptor[]? descriptors, out ushort count, uint flags);

    [DllImport("BluetoothApis.dll", ExactSpelling = true)]
    private static extern int BluetoothGATTGetDescriptorValue(SafeFileHandle service, in Descriptor descriptor,
        uint capacity, [Out] byte[] value, out ushort size, uint flags);

    internal static DeviceReading[] Read(Action<string>? diagnostic)
    {
        var nodes = new List<NodeInfo>();
        foreach (string enumerator in new[] { "BTHLE", "BTHLEDEVICE", "BTHENUM", "BTHHFENUM" })
        {
            string[] ids = DeviceIds(enumerator);
            diagnostic?.Invoke($"Bluetooth {enumerator}: {ids.Length} nodes");
            foreach (string id in ids)
            {
                if (Node(id) is not uint node)
                    continue;
                var bytes = Property(node, ContainerId, 0x0D);
                Guid? container = bytes is { Length: 16 } ? new Guid(bytes) : null;
                string? address = AddressOf(id);
                bool root = id.Split('\\', 3)[1].StartsWith("Dev_", StringComparison.OrdinalIgnoreCase);
                bool? connected = Property(node, ConnectedProperty, 0x11) is { Length: 1 } link ? link[0] != 0 : null;
                int? battery = Property(node, BatteryProperty, 3) is { Length: 1 } level && level[0] <= 100 ? level[0] : null;
                if (container.HasValue || address is not null)
                    nodes.Add(new(address, container, root, root && enumerator == "BTHENUM", Name(node), connected, battery));
            }
        }

        var services = InterfacePaths(BatteryService).Select(path => (Path: path, Address: AddressOf(path))).ToArray();
        diagnostic?.Invoke($"Bluetooth GATT Battery Service: {services.Length} interfaces");
        var results = new List<DeviceReading>();
        foreach (var root in nodes.Where(node => node.IsRoot && !string.IsNullOrWhiteSpace(node.Name))
            .DistinctBy(node => node.Container?.ToString() ?? node.Address ?? node.Name))
        {
            var group = nodes.Where(node => node == root
                || (root.Container.HasValue && node.Container == root.Container)
                || (root.Address is not null && node.Address == root.Address)).ToArray();
            var classic = group.Where(node => node.IsClassicRoot)
                .Select(node => ClassicConnected(node.Address)).ToArray();
            bool connected = group.Any(node => node.Connected == true) || classic.Contains(true);
            bool disconnected = !connected && (group.Any(node => node.Connected == false)
                || (group.Where(node => node.IsRoot).All(node => node.IsClassicRoot) && classic.Contains(false)));
            string name = root.Name!;
            var (id, kind) = Identify(name, root.Container?.ToString() ?? root.Address ?? name);
            int? battery = group.OrderByDescending(node => node.Connected == true).Select(node => node.Battery)
                .FirstOrDefault(value => value.HasValue);
            string? detail = battery.HasValue
                ? connected ? "Windows 电量" : "Windows 缓存读数"
                : null;
            var addresses = group.Where(node => node.Address is not null).Select(node => node.Address).ToHashSet();
            var matchingServices = services.Where(service => service.Address is not null && addresses.Contains(service.Address)).ToArray();
            bool readParts = kind == DeviceKind.Audio || (kind == DeviceKind.Other && matchingServices.Length > 1);
            IReadOnlyList<BatteryPart>? parts = null;
            bool? charging = null;
            if (!disconnected && matchingServices.Length > 0 && (!battery.HasValue || readParts))
            {
                var gatt = ReadGatt(matchingServices.Select(service => service.Path), connected, readParts);
                parts = gatt.Parts;
                if (parts.Count > 0)
                    kind = DeviceKind.Audio;
                charging = connected ? gatt.Charging : null;
                if (!battery.HasValue)
                {
                    battery = gatt.Total;
                    if (!connected && (battery.HasValue || parts.Count > 0))
                        detail = "蓝牙缓存读数";
                }
            }

            bool hasBattery = battery.HasValue || parts is { Count: > 0 };
            if (disconnected || (!connected && !hasBattery))
                continue;
            if (id == DeviceReader.MouseId)
                name = DeviceReader.Mouse.Name;
            else if (id == DeviceReader.KeyboardId)
                name = DeviceReader.Keyboard.Name;
            var state = !connected && detail is not null
                ? LinkState.Unknown
                : hasBattery ? LinkState.Connected : LinkState.Unavailable;
            results.Add(new(id, name, kind, "蓝牙", state, battery, charging, detail, parts));
        }
        return results.ToArray();
    }

    private static string? AddressOf(string path)
    {
        var match = DeviceAddress.Match(path);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static bool? ClassicConnected(string? address)
    {
        if (!ulong.TryParse(address, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value))
            return null;
        var device = new ClassicDevice { Size = (uint)Marshal.SizeOf<ClassicDevice>(), Address = value };
        return BluetoothGetDeviceInfo(IntPtr.Zero, ref device) == 0 ? device.Connected != 0 : null;
    }

    private static GattReading ReadGatt(IEnumerable<string> paths, bool connected, bool readParts)
    {
        var values = new List<GattValue>();
        foreach (string path in paths)
        {
            values.AddRange(ReadGattService(path, connected, readParts));
            if (!readParts && values.Count > 0)
                break;
        }
        var parts = values.Where(value => value.Part.HasValue)
            .DistinctBy(value => value.Part)
            .Select(value => new BatteryPart(value.Part!.Value, value.Percent, value.Charging))
            .OrderBy(part => part.Kind).ToArray();
        var total = values.FirstOrDefault(value => value.Part is null);
        return new(total?.Percent, parts, total?.Charging);
    }

    private static GattValue[] ReadGattService(string path, bool connected, bool readParts)
    {
        using var handle = Open(path, read: true, write: true);
        if (handle.IsInvalid)
            return [];
        _ = BluetoothGATTGetCharacteristics(handle, IntPtr.Zero, 0, null, out ushort count, 0);
        if (count is 0 or > 256)
            return [];
        var characteristics = new Characteristic[count];
        if (BluetoothGATTGetCharacteristics(handle, IntPtr.Zero, count, characteristics, out ushort actual, 0) != 0)
            return [];
        var readable = characteristics.Take(actual).Where(characteristic => characteristic.Readable != 0).ToArray();
        uint flags = connected ? ForceDevice : ForceCache;
        var readings = new List<GattValue>();
        foreach (var characteristic in readable.Where(characteristic => characteristic.Uuid.Matches(0x2A19, BatteryCharacteristic)))
        {
            var value = ReadCharacteristic(handle, characteristic, flags);
            if (value is { Length: 1 } && value[0] <= 100)
                readings.Add(new(value[0], readParts ? IdentifyDescriptor(handle, characteristic) : null, null));
        }
        if (readParts)
        {
            foreach (var characteristic in readable.Where(characteristic => characteristic.Uuid.Matches(0x2BED, BatteryStatusCharacteristic)))
            {
                var value = ReadCharacteristic(handle, characteristic, flags);
                var describedPart = readings.Count == 1 ? readings[0].Part : null;
                if (value is not null && ParseBatteryStatus(value, describedPart) is GattValue status)
                {
                    if (status.Part.HasValue && readings.Count == 1 && readings[0].Part is null)
                        readings.Clear();
                    readings.Add(status);
                }
            }
        }
        return readings.OrderByDescending(reading => reading.Charging.HasValue).ToArray();
    }

    private static byte[]? ReadCharacteristic(SafeFileHandle handle, Characteristic characteristic, uint flags)
    {
        var buffer = new byte[64];
        if (BluetoothGATTGetCharacteristicValue(handle, characteristic, (uint)buffer.Length, buffer, out ushort size, flags) != 0
            || size < sizeof(uint))
            return null;
        uint length = BitConverter.ToUInt32(buffer, 0);
        return length <= size - sizeof(uint) && length <= buffer.Length - sizeof(uint)
            ? buffer.AsSpan(sizeof(uint), (int)length).ToArray()
            : null;
    }

    private static BatteryPartKind? IdentifyDescriptor(SafeFileHandle handle, Characteristic characteristic)
    {
        _ = BluetoothGATTGetDescriptors(handle, characteristic, 0, null, out ushort count, 0);
        if (count is 0 or > 64)
            return null;
        var descriptors = new Descriptor[count];
        if (BluetoothGATTGetDescriptors(handle, characteristic, count, descriptors, out ushort actual, 0) != 0)
            return null;
        int headerLength = Marshal.SizeOf<DescriptorValue>();
        foreach (var descriptor in descriptors.Take(actual).Where(descriptor => descriptor.Type is 1 or 4)
            .OrderByDescending(descriptor => descriptor.Type))
        {
            var buffer = new byte[512];
            if (BluetoothGATTGetDescriptorValue(handle, descriptor, (uint)buffer.Length, buffer, out ushort size, 0) != 0 ||
                size < headerLength)
                continue;
            var header = MemoryMarshal.Read<DescriptorValue>(buffer);
            BatteryPartKind? part = descriptor.Type == 4
                ? header.Namespace == 1 && header.Description.IsShort != 0 ? IdentifyPart(header.Description.Short) : null
                : header.DataSize <= size - headerLength && header.DataSize <= buffer.Length - headerLength
                    ? IdentifyPart(Encoding.UTF8.GetString(buffer, headerLength, (int)header.DataSize)) : null;
            if (part.HasValue)
                return part;
        }
        return null;
    }

    internal static BatteryPartKind? IdentifyPart(ushort description) => description switch
    {
        0x010D => BatteryPartKind.LeftEarbud,
        0x010E => BatteryPartKind.RightEarbud,
        _ => null
    };

    internal static BatteryPartKind? IdentifyPart(string description)
    {
        string normalized = Regex.Replace(description.ToLowerInvariant().Trim(' ', '\0'), @"[-_\s]+", " ");
        return normalized switch
        {
            "left" or "left ear" or "left earbud" or "left earphone" or "左耳" => BatteryPartKind.LeftEarbud,
            "right" or "right ear" or "right earbud" or "right earphone" or "右耳" => BatteryPartKind.RightEarbud,
            "case" or "charging case" or "charge case" or "battery case" or "充电仓" or "充电盒" => BatteryPartKind.ChargingCase,
            _ => null
        };
    }

    private static GattValue? ParseBatteryStatus(byte[] value, BatteryPartKind? describedPart)
    {
        if (value.Length < 4 || (value[0] & 2) == 0)
            return null;
        bool hasIdentifier = (value[0] & 1) != 0;
        int levelIndex = hasIdentifier ? 5 : 3;
        if (value.Length <= levelIndex || value[levelIndex] > 100)
            return null;
        ushort powerState = BitConverter.ToUInt16(value, 1);
        if ((powerState & 1) == 0)
            return null;
        ushort identifier = hasIdentifier ? BitConverter.ToUInt16(value, 3) : (ushort)0x0106;
        var part = IdentifyPart(identifier) ?? describedPart;
        if (identifier != 0x0106 && part is null)
            return null;
        bool? charging = ((powerState >> 5) & 3) switch { 1 => true, 2 or 3 => false, _ => null };
        return new(value[levelIndex], part, charging);
    }

    private static (string Id, DeviceKind Kind) Identify(string name, string identity)
    {
        if (name.Contains("HARPE ACE", StringComparison.OrdinalIgnoreCase) && name.Contains("AIM LAB", StringComparison.OrdinalIgnoreCase))
            return (DeviceReader.MouseId, DeviceKind.Mouse);
        if (name.Contains("V87", StringComparison.OrdinalIgnoreCase) &&
            (name.Contains("VGN", StringComparison.OrdinalIgnoreCase) || name.Contains("VXE", StringComparison.OrdinalIgnoreCase)))
            return (DeviceReader.KeyboardId, DeviceKind.Keyboard);
        var kind = name.ToUpperInvariant() switch
        {
            var upper when upper.Contains("MOUSE") => DeviceKind.Mouse,
            var upper when upper.Contains("KEYBOARD") => DeviceKind.Keyboard,
            var upper when upper.Contains("BUDS") || upper.Contains("HEAD") || upper.Contains("AIRPODS")
                || upper.Contains("EARPHONE") || upper.Contains("耳机") => DeviceKind.Audio,
            _ => DeviceKind.Other
        };
        return ("bt-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16], kind);
    }
}
