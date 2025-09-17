using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VideoPackerSolution.Utils
{
    public static class UsbAuth
    {
        public const string DefaultSalt = "ca2961109a64ae06ae3500a6ff1ccab3";

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GetVolumeInformation(
            string lpRootPathName,
            System.Text.StringBuilder lpVolumeNameBuffer,
            int nVolumeNameSize,
            out uint lpVolumeSerialNumber,
            out uint lpMaximumComponentLength,
            out uint lpFileSystemFlags,
            System.Text.StringBuilder lpFileSystemNameBuffer,
            int nFileSystemNameSize);

        public static uint GetVolumeSerial(string driveRoot)
        {
            if (string.IsNullOrEmpty(driveRoot)) throw new ArgumentNullException(nameof(driveRoot));
            bool ok = GetVolumeInformation(driveRoot, null, 0, out uint serial, out _, out _, null, 0);
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"GetVolumeInformation failed for {driveRoot} (win32 error {err})");
            }
            return serial;
        }

        public static string VolumeSerialToHex(uint serial) => serial.ToString("X8");

        public static string MakeSaltedHash(string salt, string volumeSerialHex)
        {
            var text = (salt ?? "") + (volumeSerialHex ?? "");
            using var sha = SHA256.Create();
            var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            var sb = new StringBuilder(digest.Length * 2);
            foreach (var b in digest) sb.AppendFormat("{0:x2}", b);
            return sb.ToString();
        }

        public static List<string> ReadHashesFromFilePath(string path)
        {
            if (!File.Exists(path)) return new List<string>();
            var txt = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<string>>(txt);
            return list ?? new List<string>();
        }
    }
}
