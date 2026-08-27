using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SonarQuickMixer.Headset;

internal sealed class HidHeadsetStatus
{
    public required string ProductName { get; init; }
    public required ushort ProductId { get; init; }
    public bool IsHeadsetPowered { get; init; }
    public int BatteryPercent { get; init; }
    public bool IsCharging { get; init; }
}

/// <summary>
/// Reads SteelSeries Arctis Nova-family headset status over HID (usage page 0xFFC0).
/// Protocol: output report [0x00, 0xB0], response parsed as Nova 5 percentage layout.
/// </summary>
internal static class SteelSeriesHeadsetHidReader
{
    private const ushort SteelSeriesVendorId = 0x1038;
    private const ushort VendorUsagePage = 0xFFC0;
    private const byte StatusOpcode = 0xB0;
    private const byte HeadsetConnectedMarker = 0x03;
    private const byte ChargingMarker = 0x01;

    public static HidHeadsetStatus? TryReadStatus(ushort? preferredProductId = null)
    {
        try
        {
            return TryReadStatusCore(preferredProductId);
        }
        catch
        {
            return null;
        }
    }

    private static HidHeadsetStatus? TryReadStatusCore(ushort? preferredProductId)
    {
        HidNative.HidD_GetHidGuid(out var hidGuid);
        var deviceInfo = HidNative.SetupDiGetClassDevs(
            ref hidGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            HidNative.DigcfPresent | HidNative.DigcfDeviceInterface);

        if (deviceInfo == IntPtr.Zero || deviceInfo == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            HidHeadsetStatus? preferredMatch = null;
            HidHeadsetStatus? anyMatch = null;

            for (var index = 0; ; index++)
            {
                var interfaceData = new HidNative.SpDeviceInterfaceData
                {
                    CbSize = Marshal.SizeOf<HidNative.SpDeviceInterfaceData>()
                };

                if (!HidNative.SetupDiEnumDeviceInterfaces(
                        deviceInfo,
                        IntPtr.Zero,
                        ref hidGuid,
                        index,
                        ref interfaceData))
                {
                    break;
                }

                var path = GetDevicePath(deviceInfo, ref interfaceData);
                if (string.IsNullOrWhiteSpace(path) ||
                    path.IndexOf("VID_1038", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                using var handle = HidNative.CreateFile(
                    path,
                    HidNative.GenericRead | HidNative.GenericWrite,
                    HidNative.FileShareRead | HidNative.FileShareWrite,
                    IntPtr.Zero,
                    HidNative.OpenExisting,
                    0,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    continue;
                }

                var attrs = new HidNative.HiddAttributes
                {
                    Size = Marshal.SizeOf<HidNative.HiddAttributes>()
                };
                if (!HidNative.HidD_GetAttributes(handle, ref attrs) ||
                    attrs.VendorId != SteelSeriesVendorId)
                {
                    continue;
                }

                if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed))
                {
                    continue;
                }

                ushort usagePage;
                ushort inputLen;
                ushort outputLen;
                try
                {
                    // HidP_GetCaps returns NTSTATUS; HIDP_STATUS_SUCCESS is 0x00110000.
                    _ = HidNative.HidP_GetCaps(preparsed, out var caps);
                    usagePage = caps.UsagePage;
                    inputLen = caps.InputReportByteLength;
                    outputLen = caps.OutputReportByteLength;
                }
                finally
                {
                    HidNative.HidD_FreePreparsedData(preparsed);
                }

                if (usagePage != VendorUsagePage || inputLen < 6 || outputLen < 2)
                {
                    continue;
                }

                var status = TryPoll(handle, attrs.ProductId, inputLen, outputLen);
                if (status is null)
                {
                    continue;
                }

                if (preferredProductId is not null && attrs.ProductId == preferredProductId.Value)
                {
                    preferredMatch = status;
                    break;
                }

                anyMatch ??= status;
            }

            return preferredMatch ?? anyMatch;
        }
        finally
        {
            HidNative.SetupDiDestroyDeviceInfoList(deviceInfo);
        }
    }

    private static HidHeadsetStatus? TryPoll(
        SafeFileHandle handle,
        ushort productId,
        ushort inputLen,
        ushort outputLen)
    {
        var output = new byte[outputLen];
        output[0] = 0x00;
        output[1] = StatusOpcode;

        if (!HidNative.WriteFile(handle, output, output.Length, out _, IntPtr.Zero))
        {
            // Some stacks prefer SetOutputReport; fall through either way.
            HidNative.HidD_SetOutputReport(handle, output, output.Length);
        }
        else
        {
            HidNative.HidD_SetOutputReport(handle, output, output.Length);
        }

        var input = new byte[inputLen];
        input[0] = 0x00;
        if (!HidNative.HidD_GetInputReport(handle, input, input.Length))
        {
            return null;
        }

        // Report ID may be present as byte 0.
        var offset = input[0] == StatusOpcode ? 0 : 1;
        if (offset + 4 >= input.Length || input[offset] != StatusOpcode)
        {
            return null;
        }

        var connected = input[offset + 1] == HeadsetConnectedMarker;
        var battery = input[offset + 3];
        var charging = input[offset + 4] == ChargingMarker;

        if (battery > 100)
        {
            return null;
        }

        var productName = TryGetProductString(handle) ?? $"SteelSeries device (0x{productId:X4})";

        return new HidHeadsetStatus
        {
            ProductName = productName,
            ProductId = productId,
            IsHeadsetPowered = connected,
            BatteryPercent = battery,
            IsCharging = charging
        };
    }

    private static string? TryGetProductString(SafeFileHandle handle)
    {
        var buffer = new byte[256];
        if (!HidNative.HidD_GetProductString(handle, buffer, buffer.Length))
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? GetDevicePath(IntPtr deviceInfo, ref HidNative.SpDeviceInterfaceData interfaceData)
    {
        HidNative.SetupDiGetDeviceInterfaceDetail(
            deviceInfo,
            ref interfaceData,
            IntPtr.Zero,
            0,
            out var required,
            IntPtr.Zero);

        if (required <= 0)
        {
            return null;
        }

        var detailPtr = Marshal.AllocHGlobal(required);
        try
        {
            Marshal.WriteInt32(detailPtr, IntPtr.Size == 8 ? 8 : 6);
            if (!HidNative.SetupDiGetDeviceInterfaceDetail(
                    deviceInfo,
                    ref interfaceData,
                    detailPtr,
                    required,
                    out _,
                    IntPtr.Zero))
            {
                return null;
            }

            return Marshal.PtrToStringAuto(IntPtr.Add(detailPtr, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(detailPtr);
        }
    }

    private static class HidNative
    {
        public const int DigcfPresent = 0x2;
        public const int DigcfDeviceInterface = 0x10;
        public const int GenericRead = unchecked((int)0x80000000);
        public const int GenericWrite = 0x40000000;
        public const int FileShareRead = 0x1;
        public const int FileShareWrite = 0x2;
        public const int OpenExisting = 3;

        [StructLayout(LayoutKind.Sequential)]
        public struct SpDeviceInterfaceData
        {
            public int CbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HiddAttributes
        {
            public int Size;
            public ushort VendorId;
            public ushort ProductId;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HiddAttributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetProductString(SafeFileHandle hidDeviceObject, byte[] buffer, int bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetOutputReport(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetInputReport(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid,
            IntPtr enumerator,
            IntPtr hwndParent,
            int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            ref Guid interfaceClassGuid,
            int memberIndex,
            ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet,
            ref SpDeviceInterfaceData deviceInterfaceData,
            IntPtr deviceInterfaceDetailData,
            int deviceInterfaceDetailDataSize,
            out int requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName,
            int dwDesiredAccess,
            int dwShareMode,
            IntPtr lpSecurityAttributes,
            int dwCreationDisposition,
            int dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(
            SafeFileHandle hFile,
            byte[] lpBuffer,
            int nNumberOfBytesToWrite,
            out int lpNumberOfBytesWritten,
            IntPtr lpOverlapped);
    }
}
