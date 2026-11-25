using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace BatteryWidget;

internal static class NativeDevices
{
    private const uint Read = 0x80000000;
    private const uint Write = 0x40000000;
    private const uint Overlapped = 0x40000000;
    private const uint DevicePresent = 0x100;
    private const uint FilterEnumerator = 1;
    private const int BufferSmall = 0x1A;

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct PropertyKey(Guid Format, uint Id);

    internal static readonly Guid DeviceProperties = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    internal static readonly Guid ContainerProperties = new("78c34fc8-104a-4aca-9ea4-524d52996e57");

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidCaps
    {
        public ushort Usage, Page, Input, Output, Feature;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)]
        private ushort[] reservedAndCounts;
    }

    internal sealed record HidInterface(string Path, string Product, ushort Vendor, ushort ProductId, HidCaps Caps)
    {
        public string InterfaceName => Path.Split('#')[1];
        public string PairGroup => Path.Split('#')[2].Split('&').Length >= 3
            ? string.Join('&', Path.Split('#')[2].Split('&')[..3])
            : Path.Split('#')[2];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public uint Size;
        public ushort Vendor, Product, Version;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, int length);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
    [DllImport("hid.dll")]
    private static extern byte HidD_FreePreparsedData(IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint size, in Guid id, string? instance, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_Interface_ListW(in Guid id, string? instance, [Out] char[] buffer, uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint size, string enumerator, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Get_Device_ID_ListW(string enumerator, [Out] char[] buffer, uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Locate_DevNodeW(out uint node, string instance, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern int CM_Get_DevNode_PropertyW(uint node, in PropertyKey key, out uint type, [Out] byte[] buffer,
        ref uint size, uint flags);

    internal static SafeFileHandle Open(string path, bool read, bool write = false, bool asynchronous = false) =>
        CreateFileW(path, (read ? Read : 0) | (write ? Write : 0), 3, IntPtr.Zero, 3, asynchronous ? Overlapped : 0, IntPtr.Zero);

    internal static string[] InterfacePaths(Guid id)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint size, id, null, 0) != 0 || size <= 1)
                return [];
            var buffer = new char[size];
            int result = CM_Get_Device_Interface_ListW(id, null, buffer, size, 0);
            if (result != BufferSmall)
                return result == 0 ? SplitList(buffer) : [];
        }
        return [];
    }

    internal static string[] DeviceIds(string enumerator)
    {
        uint flags = DevicePresent | FilterEnumerator;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint size, enumerator, flags) != 0 || size <= 1)
                return [];
            var buffer = new char[size];
            int result = CM_Get_Device_ID_ListW(enumerator, buffer, size, flags);
            if (result != BufferSmall)
                return result == 0 ? SplitList(buffer) : [];
        }
        return [];
    }

    private static string[] SplitList(char[] buffer) => new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    internal static uint? Node(string id) => CM_Locate_DevNodeW(out uint node, id, 0) == 0 ? node : null;

    internal static byte[]? Property(uint node, PropertyKey key, uint expectedType)
    {
        var buffer = new byte[512];
        uint length = (uint)buffer.Length;
        if (CM_Get_DevNode_PropertyW(node, key, out uint type, buffer, ref length, 0) != 0 || type != expectedType)
            return null;
        return buffer[..(int)length];
    }

    internal static string? Name(uint node)
    {
        var value = Property(node, new(DeviceProperties, 14), 0x12);
        return value is null ? null : Encoding.Unicode.GetString(value).TrimEnd('\0');
    }

    internal static HidInterface[] HidInterfaces()
    {
        HidD_GetHidGuid(out Guid id);
        var interfaces = new System.Collections.Generic.List<HidInterface>();
        foreach (string path in InterfacePaths(id))
        {
            using SafeFileHandle handle = Open(path, false);
            if (handle.IsInvalid)
                continue;
            var attributes = new HidAttributes { Size = (uint)Marshal.SizeOf<HidAttributes>() };
            if (!HidD_GetAttributes(handle, ref attributes) || !HidD_GetPreparsedData(handle, out IntPtr data))
                continue;
            try
            {
                if (HidP_GetCaps(data, out HidCaps caps) != 0x00110000)
                    continue;
                var buffer = new byte[256];
                string product = HidD_GetProductString(handle, buffer, buffer.Length)
                    ? Encoding.Unicode.GetString(buffer).Split('\0', 2)[0]
                    : "";
                interfaces.Add(new(path, product, attributes.Vendor, attributes.Product, caps));
            }
            finally
            {
                HidD_FreePreparsedData(data);
            }
        }
        return interfaces.ToArray();
    }

    internal static async Task<byte[]?> QueryAsync(HidInterface device, byte[] command, Func<byte[], bool> matches)
    {
        if (device.Caps.Input == 0 || device.Caps.Output < command.Length)
            return null;
        using SafeFileHandle handle = Open(device.Path, true, true, true);
        if (handle.IsInvalid)
            return null;
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        var output = new byte[device.Caps.Output];
        command.CopyTo(output, 0);
        try
        {
            await using var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
            await stream.WriteAsync(output, budget.Token).ConfigureAwait(false);
            var input = new byte[device.Caps.Input];
            while (!budget.IsCancellationRequested)
            {
                int count = await stream.ReadAsync(input, budget.Token).ConfigureAwait(false);
                if (count == 0)
                    return null;
                if (count == input.Length && matches(input))
                    return input;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
        }
        return null;
    }
}
