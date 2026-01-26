// Install-Package System.Management
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management; // WMI
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VideoPackerSolution.Utils
{
    public record UsbDrive(string DriveLetter, string VolumeName, string HardwareSerial, string Caption);

    public static class UsbAuth
    {
        public const string DefaultSalt = "ca2961109a64ae06ae3500a6ff1ccab3";

        // 1) Lấy danh sách ổ di động (logical) kiểu Removable (DriveType = 2)
        public static List<(string DriveLetter, string VolumeName)> GetRemovableLogicalDrives()
        {
            var result = new List<(string, string)>();
            foreach (var di in DriveInfo.GetDrives())
            {
                try
                {
                    if (di.DriveType == DriveType.Removable && di.IsReady)
                    {
                        result.Add((di.RootDirectory.FullName.TrimEnd('\\'), di.VolumeLabel ?? ""));
                    }
                }
                catch { /* ignore */ }
            }
            return result;
        }

        // 2) Lấy danh sách USB Physical Disks + SerialNumber + Caption
        public static Dictionary<string, (string Serial, string Caption)> GetUsbDiskDrives()
        {
            var dict = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

            // Win32_DiskDrive where InterfaceType='USB'
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, SerialNumber, Caption FROM Win32_DiskDrive WHERE InterfaceType = 'USB'");
            foreach (ManagementObject d in searcher.Get())
            {
                var deviceId = (d["DeviceID"] as string)?.Trim();              // e.g. \\.\PHYSICALDRIVE2
                var serial = (d["SerialNumber"] as string)?.Trim() ?? "";
                var caption = (d["Caption"] as string)?.Trim() ?? "USB Drive";
                if (!string.IsNullOrEmpty(deviceId))
                    dict[deviceId] = (serial, caption);
            }
            return dict;
        }

        // 3) Map Logical (E:) -> Partition -> PhysicalDrive (\\.\PHYSICALDRIVE#)
        public static Dictionary<string, string> MapLogicalToPhysical()
        {
            // LogicalDisk -> LogicalDiskToPartition -> DiskPartition -> DiskDriveToDiskPartition -> DiskDrive
            var mapLogicalToPartition = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // "E:" -> "Disk #2, Partition #1"
            using (var rel1 = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDiskToPartition"))
            {
                foreach (ManagementObject m in rel1.Get())
                {
                    var dep = (m["Dependent"] as string) ?? "";   // Win32_LogicalDisk.DeviceID="E:"
                    var ant = (m["Antecedent"] as string) ?? "";  // Win32_DiskPartition.DeviceID="Disk #2, Partition #1"
                    var drive = ExtractBetween(dep, "Win32_LogicalDisk.DeviceID=\"", "\"");
                    var part = ExtractBetween(ant, "Win32_DiskPartition.DeviceID=\"", "\"");
                    if (!string.IsNullOrEmpty(drive) && !string.IsNullOrEmpty(part))
                        mapLogicalToPartition[drive] = part;
                }
            }

            // Partition -> PhysicalDrive
            var mapPartitionToPhysical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var rel2 = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDriveToDiskPartition"))
            {
                foreach (ManagementObject m in rel2.Get())
                {
                    var dep = (m["Dependent"] as string) ?? "";   // Win32_DiskPartition.DeviceID="Disk #2, Partition #1"
                    var ant = (m["Antecedent"] as string) ?? "";  // Win32_DiskDrive.DeviceID="\\.\PHYSICALDRIVE2"
                    var part = ExtractBetween(dep, "Win32_DiskPartition.DeviceID=\"", "\"");
                    var drive = ExtractBetween(ant, "Win32_DiskDrive.DeviceID=\"", "\"");
                    if (!string.IsNullOrEmpty(part) && !string.IsNullOrEmpty(drive))
                        mapPartitionToPhysical[part] = drive;
                }
            }

            // Join
            var mapLogicalToPhysical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in mapLogicalToPartition)
            {
                if (mapPartitionToPhysical.TryGetValue(kv.Value, out var physical))
                    mapLogicalToPhysical[kv.Key] = physical;
            }
            return mapLogicalToPhysical;

            static string ExtractBetween(string input, string start, string end)
            {
                var i = input.IndexOf(start, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return "";
                i += start.Length;
                var j = input.IndexOf(end, i, StringComparison.OrdinalIgnoreCase);
                if (j < 0) return "";
                return input.Substring(i, j - i);
            }
        }

        // 4) Tổng hợp danh sách UsbDrive: E: + VolumeName + Hardware Serial + Caption
        public record UsbDrive(string DriveLetter, string VolumeName, string HardwareSerial, string Caption);

        // 4) Tổng hợp danh sách UsbDrive: E: + VolumeName + Hardware Serial + Caption
        // using System.Management;
        // using System.Management;

        public static List<UsbDrive> GetUsbDrivesWithSerial()
        {
            var result = new List<UsbDrive>();

            using var driveSearcher = new ManagementObjectSearcher(
                "SELECT DeviceID, SerialNumber, Caption FROM Win32_DiskDrive WHERE InterfaceType='USB'");
            foreach (ManagementObject disk in driveSearcher.Get())
            {
                var deviceId = ((string?)disk["DeviceID"])?.Trim();           // \\.\PHYSICALDRIVE#
                var serial = ((string?)disk["SerialNumber"])?.Trim() ?? "";
                var caption = ((string?)disk["Caption"])?.Trim() ?? "USB Drive";

                if (string.IsNullOrEmpty(deviceId))
                    continue;
                var qPart = $@"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID=""{deviceId.Replace(@"\", @"\\")}""}}
                       WHERE AssocClass = Win32_DiskDriveToDiskPartition";
                using var partSearcher = new ManagementObjectSearcher(qPart);
                var anyLetter = false;

                foreach (ManagementObject part in partSearcher.Get())
                {
                    var partDeviceId = ((string?)part["DeviceID"])?.Trim(); // "Disk #2, Partition #1"
                    if (string.IsNullOrEmpty(partDeviceId)) continue;
                    var qLog = $@"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID=""{partDeviceId}""}}
                          WHERE AssocClass = Win32_LogicalDiskToPartition";
                    using var logSearcher = new ManagementObjectSearcher(qLog);
                    foreach (ManagementObject log in logSearcher.Get())
                    {
                        var driveLetter = ((string?)log["DeviceID"])?.Trim(); // "E:"
                        string volumeName = "";
                        try { volumeName = ((string?)log["VolumeName"])?.Trim() ?? ""; } catch { }

                        if (!string.IsNullOrEmpty(driveLetter))
                        {
                            anyLetter = true;
                            result.Add(new UsbDrive(
                                driveLetter,
                                volumeName,
                                serial,
                                caption
                            ));
                        }
                    }
                }

                if (!anyLetter)
                {
                    // Không có drive letter vẫn add item để còn so hash theo hardware serial
                    result.Add(new UsbDrive(
                        "",          // DriveLetter
                        "",          // VolumeName
                        serial,      // HardwareSerial
                        caption      // Caption
                    ));
                }
            }

            return result;
        }

        // 5) Tạo SHA-256(salt + hardware_serial)
        public static string MakeSaltedHash(string salt, string hardwareSerial)
        {
            var text = (salt ?? "") + (hardwareSerial ?? "");
            using var sha = SHA256.Create();
            var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            var sb = new StringBuilder(digest.Length * 2);
            foreach (var b in digest) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // 6) Đọc danh sách hash kỳ vọng (giống Rust include_str!)
        public static List<string> ReadHashesFromFilePath(string path)
        {
            if (!File.Exists(path)) return new List<string>();
            var txt = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<string>>(txt) ?? new List<string>();
        }

        // 7) Validate: có USB nào tạo hash khớp?
        public static bool ValidateUsbByHardwareSerial(IEnumerable<string> expectedHashes, string salt = DefaultSalt, Action<string>? log = null)
        {
            var exp = expectedHashes?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (exp.Count == 0) return false;

            var drives = GetUsbDrivesWithSerial();
            if (drives.Count == 0) return false;

            foreach (var d in drives)
            {
                var h = MakeSaltedHash(salt, d.HardwareSerial);
                log?.Invoke($"USB {d.DriveLetter} [{d.Caption}] SN='{d.HardwareSerial}' → hash={h}");
                if (exp.Contains(h)) return true;
            }
            return false;
        }

        // P/Invoke: open \\.\E: and get device number (PhysicalDriveN)
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            int nInBufferSize,
            out STORAGE_DEVICE_NUMBER lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_DEVICE_NUMBER
        {
            public uint DeviceType;
            public uint DeviceNumber; // THIS is PhysicalDriveN
            public uint PartitionNumber;
        }

        public static bool TryGetHardwareSerialFromDriveRoot(string driveRoot, out string hardwareSerial, out string caption, out string physicalDrivePath, out string debugInfo)
        {
            hardwareSerial = "";
            caption = "";
            physicalDrivePath = "";
            debugInfo = "";

            try
            {
                if (string.IsNullOrWhiteSpace(driveRoot)) { debugInfo = "Empty driveRoot."; return false; }

                // Normalize "E:\" -> "\\.\E:"
                var root = driveRoot.Trim();
                if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
                var volumePath = $@"\\.\{root.TrimEnd('\\')}";

                // 1) Map E:\ -> PhysicalDriveN
                using var h = CreateFile(volumePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                         IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                if (h.IsInvalid) { debugInfo = $"CreateFile({volumePath}) failed. Win32={Marshal.GetLastWin32Error()}"; return false; }

                if (!DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0,
                                     out STORAGE_DEVICE_NUMBER devnum, Marshal.SizeOf<STORAGE_DEVICE_NUMBER>(),
                                     out var _, IntPtr.Zero))
                {
                    debugInfo = $"DeviceIoControl(IOCTL_STORAGE_GET_DEVICE_NUMBER) failed. Win32={Marshal.GetLastWin32Error()}";
                    return false;
                }

                physicalDrivePath = $@"\\.\PHYSICALDRIVE{devnum.DeviceNumber}";

                // 2) Query WMI for that PhysicalDrive
                using var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, SerialNumber, PNPDeviceID, Caption FROM Win32_DiskDrive WHERE DeviceID = @p");
                var q = searcher.Query as ObjectQuery;
                // Workaround: System.Management doesn't support @p; build a literal query safely:
                var wmiQuery = $"SELECT DeviceID, SerialNumber, PNPDeviceID, Caption FROM Win32_DiskDrive WHERE DeviceID = '{physicalDrivePath.Replace(@"\", @"\\")}'";
                using var searcher2 = new ManagementObjectSearcher(wmiQuery);

                foreach (ManagementObject d in searcher2.Get())
                {
                    string devId = (d["DeviceID"] as string) ?? "";
                    string sn = (d["SerialNumber"] as string) ?? "";
                    string pnp = (d["PNPDeviceID"] as string) ?? "";
                    string cap = (d["Caption"] as string) ?? "USB Drive";

                    caption = cap;

                    // 3) Prefer SerialNumber
                    if (!string.IsNullOrWhiteSpace(sn))
                    {
                        hardwareSerial = sn.Trim();
                        debugInfo = "Source=Win32_DiskDrive.SerialNumber";
                        return true;
                    }

                    // 4) Fallback: Win32_PhysicalMedia.SerialNumber (Tag = \\.\PHYSICALDRIVE#)
                    var tagLiteral = physicalDrivePath.Replace(@"\", @"\\");
                    var q2 = $"SELECT SerialNumber FROM Win32_PhysicalMedia WHERE Tag = '{tagLiteral}'";
                    using (var s2 = new ManagementObjectSearcher(q2))
                    {
                        foreach (ManagementObject m in s2.Get())
                        {
                            var sn2 = (m["SerialNumber"] as string) ?? "";
                            if (!string.IsNullOrWhiteSpace(sn2))
                            {
                                hardwareSerial = sn2.Trim();
                                debugInfo = "Source=Win32_PhysicalMedia.SerialNumber";
                                return true;
                            }
                        }
                    }
                    // 5) Fallback: extract last token from PNPDeviceID (many USB expose serial here)
                    // Example PNPDeviceID: "USBSTOR\DISK&VEN_SANDISK&PROD_EXTREME&REV_1.00\AA01112233445566&0"
                    if (!string.IsNullOrWhiteSpace(pnp))
                    {
                        var last = pnp.Split('\\').LastOrDefault() ?? "";
                        // strip trailing &0 etc.
                        var cut = last.Split('&').FirstOrDefault() ?? last;
                        cut = cut.Trim();
                        if (!string.IsNullOrWhiteSpace(cut))
                        {
                            hardwareSerial = cut;
                            debugInfo = "Source=PNPDeviceID(parsed)";
                            return true;
                        }
                    }
                }

                debugInfo = "WMI returned no rows for DeviceID=" + physicalDrivePath;
                return false;
            }
            catch (Exception ex)
            {
                debugInfo = "Exception: " + ex.Message;
                return false;
            }
        }
    }
}
