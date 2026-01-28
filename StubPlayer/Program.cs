using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoPackerSolution.Utils;

class Program
{
    const string PayloadMagicString = "VIDPKG1\0";
    const string MetaMarkerString = "HASHPKG1";

    static readonly byte[] PayloadMagic = Encoding.ASCII.GetBytes(PayloadMagicString);
    static readonly byte[] MetaMarker = Encoding.ASCII.GetBytes(MetaMarkerString);

    static int Main()
    {
        try
        {
            // CLEANUP TEMP TỪ LẦN CHẠY TRƯỚC
            CleanupOldVpkgFolders();
            // 1) USB VALIDATION
            var (ok, _) = ValidateUsbViaEmbeddedHashes();
            if (!ok) return 2;

            // 2) OPEN SELF
            string exePath =
                Environment.ProcessPath ??
                Process.GetCurrentProcess().MainModule?.FileName ??
                throw new Exception("exe path");

            using var fs = File.OpenRead(exePath);

            // 3) FIND PAYLOAD MARKER (STREAMING, FILE LỚN OK)
            long pos = FindLastPatternPosition(fs, PayloadMagic);
            if (pos < 0 || pos < 8) return 3;

            // 4) READ PAYLOAD LENGTH
            fs.Seek(pos - 8, SeekOrigin.Begin);
            byte[] lenBuf = new byte[8];
            fs.Read(lenBuf, 0, 8);
            long payloadLen = BitConverter.ToInt64(lenBuf, 0);
            if (payloadLen <= 0 || payloadLen > fs.Length) return 4;

            // 5) READ PAYLOAD
            long payloadStart = pos - 8 - payloadLen;
            fs.Seek(payloadStart, SeekOrigin.Begin);
            byte[] payload = new byte[payloadLen];
            fs.Read(payload, 0, payload.Length);

            // 6) PAYLOAD PHẢI LÀ ZIP
            if (payload.Length < 4 || payload[0] != 0x50 || payload[1] != 0x4B)
                return 5;

            // 7) EXTRACT TO TEMP
            string tmpDir = Path.Combine(
                Path.GetTempPath(),
                "vpkg_" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(tmpDir);

            using (var ms = new MemoryStream(payload))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var e in zip.Entries)
                {
                    string outPath = Path.Combine(tmpDir, e.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    e.ExtractToFile(outPath, true);
                }
            }

            // 8) FIND VIDEO
            var videoFile = Directory
                .EnumerateFiles(tmpDir, "*.*", SearchOption.AllDirectories)
                .FirstOrDefault(f =>
                    f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".avi", StringComparison.OrdinalIgnoreCase)
                );

            if (videoFile == null) return 10;

            // 9) OPEN VIDEO WITH DEFAULT PLAYER
            Process.Start(new ProcessStartInfo
            {
                FileName = videoFile,
                UseShellExecute = true
            });

            // ❌ KHÔNG WaitForExit
            // ❌ KHÔNG xoá temp ở đây (tránh lỗi storage)

            return 0;
        }
        catch
        {
            return 99;
        }
    }

    // ===================== HELPERS =====================

    static long FindLastPatternPosition(FileStream fs, byte[] pattern)
    {
        const int BLOCK = 1024 * 1024; // 1MB
        long len = fs.Length;
        int pLen = pattern.Length;

        byte[] buf = new byte[BLOCK + pLen];
        long pos = len;

        while (pos > 0)
        {
            int read = (int)Math.Min(BLOCK, pos);
            pos -= read;

            fs.Seek(pos, SeekOrigin.Begin);
            fs.Read(buf, 0, read + pLen);

            for (int i = read; i >= 0; i--)
            {
                bool ok = true;
                for (int j = 0; j < pLen; j++)
                {
                    if (buf[i + j] != pattern[j])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok) return pos + i;
            }
        }
        return -1;
    }

    static (bool, string) ValidateUsbViaEmbeddedHashes()
    {
        try
        {
            string exePath =
                Environment.ProcessPath ??
                Process.GetCurrentProcess().MainModule?.FileName ??
                throw new Exception("exe");

            using var fs = File.OpenRead(exePath);

            long pos = FindLastPatternPosition(fs, PayloadMagic);
            if (pos < 0) return (false, "no payload");

            long metaPos = pos + PayloadMagic.Length;
            if (metaPos + MetaMarker.Length > fs.Length)
                return (false, "no meta");

            fs.Seek(metaPos, SeekOrigin.Begin);
            byte[] meta = new byte[MetaMarker.Length];
            fs.Read(meta, 0, meta.Length);
            if (!meta.SequenceEqual(MetaMarker))
                return (false, "meta mismatch");

            byte[] lenBuf = new byte[8];
            fs.Read(lenBuf, 0, 8);
            long len = BitConverter.ToInt64(lenBuf, 0);
            if (len <= 0) return (false, "bad len");

            byte[] json = new byte[len];
            fs.Read(json, 0, json.Length);
            var hashes = JsonSerializer.Deserialize<List<string>>(Encoding.UTF8.GetString(json));
            if (hashes == null || hashes.Count == 0)
                return (false, "empty hash");

            var drives = UsbAuth.GetUsbDrivesWithSerial();
            if (drives != null)
            {
                foreach (var d in drives)
                {
                    var sn = d.HardwareSerial?.Trim();
                    if (string.IsNullOrEmpty(sn)) continue;

                    var h = UsbAuth.MakeSaltedHash(UsbAuth.DefaultSalt, sn);
                    if (hashes.Any(x => x.Equals(h, StringComparison.OrdinalIgnoreCase)))
                        return (true, "ok");
                }
            }
        }
        catch { }

        return (false, "usb invalid");
    }

    static void CleanupOldVpkgFolders()
    {
        try
        {
            string tempPath = Path.GetTempPath();

            foreach (var dir in Directory.GetDirectories(tempPath, "vpkg_*"))
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch
                {
                    // ignore folders that are in use
                }
            }
        }
        catch
        {
            // ignore all errors
        }
    }

}
