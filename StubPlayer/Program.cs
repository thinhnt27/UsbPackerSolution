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

    static int Main(string[] args)
    {
        // 1) validate USB using embedded hashes
        var (ok, msg) = ValidateUsbViaEmbeddedHashes();
        Console.WriteLine(msg);
        if (!ok)
        {
            Console.WriteLine("Exiting. Press any key to exit...");
            Console.ReadKey();
            return 2;
        }

        // 2) read payload and play (same as before)
        string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(exePath)) { Console.WriteLine("Cannot determine exe path."); return 3; }

        try
        {
            using var fs = File.OpenRead(exePath);
            long pos = FindLastPatternPosition(fs, PayloadMagic);
            if (pos < 0) { Console.WriteLine("No payload marker found."); return 4; }

            if (pos < 8) { Console.WriteLine("Invalid format, no payload length."); return 5; }
            fs.Seek(pos - 8, SeekOrigin.Begin);
            byte[] lenBuf = new byte[8]; fs.Read(lenBuf, 0, 8);
            long payloadLen = BitConverter.ToInt64(lenBuf, 0);
            if (payloadLen <= 0 || payloadLen > fs.Length) { Console.WriteLine("Invalid payload length."); return 6; }

            long payloadStart = pos - 8 - payloadLen;
            fs.Seek(payloadStart, SeekOrigin.Begin);
            byte[] payload = new byte[payloadLen];
            fs.Read(payload, 0, payload.Length);

            // determine if zip or encrypted
            bool isZip = payload.Length >= 4 && payload[0] == 0x50 && payload[1] == 0x4B && payload[2] == 0x03 && payload[3] == 0x04;

            byte[] zipBytes;
            if (isZip) zipBytes = payload;
            else
            {
                Console.Write("Payload appears encrypted. Enter password: ");
                string? pass = ReadPasswordFromConsole();
                try { zipBytes = DecryptWithPassword(payload, pass ?? string.Empty); }
                catch (Exception ex) { Console.WriteLine("Decryption failed: " + ex.Message); return 7; }
            }

            // extract to temp and open
            var tmpDir = Path.Combine(Path.GetTempPath(), "vpkg_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            using (var ms = new MemoryStream(zipBytes))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    var outPath = Path.Combine(tmpDir, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    entry.ExtractToFile(outPath);
                    Console.WriteLine("Extracted: " + outPath);
                }
            }

            var started = new List<Process?>();
            foreach (var f in Directory.EnumerateFiles(tmpDir, "*", SearchOption.AllDirectories))
            {
                Console.WriteLine("Opening: " + f);
                var psi = new ProcessStartInfo(f) { UseShellExecute = true };
                try { var p = Process.Start(psi); started.Add(p); }
                catch (Exception ex) { Console.WriteLine("Failed to start file: " + ex.Message); }
            }

            bool waitedAny = false;
            foreach (var p in started)
            {
                if (p == null) continue;
                try { p.WaitForExit(); waitedAny = true; } catch { }
            }

            if (!waitedAny)
            {
                Console.WriteLine("Press any key to cleanup temporary files and exit...");
                Console.ReadKey();
            }

            try { Directory.Delete(tmpDir, true); Console.WriteLine("Temporary files removed."); }
            catch (Exception ex) { Console.WriteLine("Failed to delete temp folder: " + ex.Message); }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error processing payload: " + ex);
            return 9;
        }
    }

    // find last occurrence of a pattern in the file
    static long FindLastPatternPosition(FileStream fs, byte[] pattern)
    {
        const long SAFEMAX = 500 * 1024 * 1024;
        long len = fs.Length;
        if (len > SAFEMAX) throw new Exception("File too large to search safely.");
        fs.Seek(0, SeekOrigin.Begin);
        byte[] all = new byte[len];
        int r = fs.Read(all, 0, all.Length);
        for (long i = len - pattern.Length; i >= 0; i--)
        {
            bool ok = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (all[i + j] != pattern[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    // read embedded hashes and compare with current drive
    static (bool, string) ValidateUsbViaEmbeddedHashes()
    {
        try
        {
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? AppContext.BaseDirectory;
            using var fs = File.OpenRead(exePath);
            long pos = FindLastPatternPosition(fs, PayloadMagic);
            if (pos < 0) return (false, "No payload marker found in exe.");

            if (pos < 8) return (false, "Invalid exe format (missing payload length).");
            fs.Seek(pos - 8, SeekOrigin.Begin);
            byte[] lenBuf = new byte[8]; fs.Read(lenBuf, 0, 8);
            long payloadLen = BitConverter.ToInt64(lenBuf, 0);

            long markerEnd = pos + PayloadMagic.Length;
            if (markerEnd + MetaMarker.Length > fs.Length) return (false, "No embedded metadata found.");

            fs.Seek(markerEnd, SeekOrigin.Begin);
            byte[] maybeMeta = new byte[MetaMarker.Length];
            fs.Read(maybeMeta, 0, maybeMeta.Length);
            if (!maybeMeta.SequenceEqual(MetaMarker)) return (false, "No embedded hashes metadata found in exe.");

            // read hashes_len (8 bytes)
            byte[] lenBuf2 = new byte[8];
            fs.Read(lenBuf2, 0, 8);
            long hashesLen = BitConverter.ToInt64(lenBuf2, 0);
            if (hashesLen <= 0 || markerEnd + MetaMarker.Length + 8 + hashesLen > fs.Length) return (false, "Invalid embedded hashes length.");

            var hashesBytes = new byte[hashesLen];
            fs.Read(hashesBytes, 0, hashesBytes.Length);
            string hashesJson = Encoding.UTF8.GetString(hashesBytes);

            var list = JsonSerializer.Deserialize<List<string>>(hashesJson) ?? new List<string>();
            if (list.Count == 0) return (false, "Embedded hashes list empty.");

            var usbDrives = UsbAuth.GetUsbDrivesWithSerial(); // cần System.Management
            if (usbDrives == null || usbDrives.Count == 0)
            {
                // Fallback: thử ngay ổ đang chạy exe
                var exeRoot = Path.GetPathRoot(exePath);
                if (!string.IsNullOrEmpty(exeRoot))
                {
                    if (UsbAuth.TryGetHardwareSerialFromDriveRoot(exeRoot, out var hwSerial, out var cap, out var phys, out var dbg))
                    {
                        var genHash = UsbAuth.MakeSaltedHash(UsbAuth.DefaultSalt, hwSerial);
                        foreach (var h in list)
                        {
                            if (string.Equals(h, genHash, StringComparison.OrdinalIgnoreCase))
                                return (true, $"USB validated (fallback by exe drive): {exeRoot} [{cap}]");
                        }
                        // Nếu chạy từ USB nhưng không khớp hash
                        return (false, $"USB not authorized (fallback). gen={genHash}");
                    }
                    // Thêm chút thông tin debug thay vì trả “No USB drives detected.”
                    return (false, $"No USB drives detected (fallback failed). exeRoot={exeRoot}");
                }

                return (false, "No USB drives detected (no exe root).");
            }

            foreach (var d in usbDrives)
            {
                var sn = (d.HardwareSerial ?? "").Trim();
                if (sn.Length == 0) continue;

                string genHash = UsbAuth.MakeSaltedHash(UsbAuth.DefaultSalt, sn);
                foreach (var h in list)
                {
                    if (string.Equals(h, genHash, StringComparison.OrdinalIgnoreCase))
                        return (true, $"USB validated: {d.DriveLetter} [{d.Caption}]");
                }
            }

            return (false, "USB not authorized. No hardware serial matched embedded hashes.");
        }
        catch (Exception ex)
        {
            return (false, "Validation error: " + ex.Message);
        }
    }

    static string? ReadPasswordFromConsole()
    {
        var pwd = new StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace && pwd.Length > 0)
            {
                pwd.Length--;
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                pwd.Append(key.KeyChar);
                Console.Write("*");
            }
        }
        Console.WriteLine();
        return pwd.ToString();
    }

    static byte[] DecryptWithPassword(byte[] payload, string password)
    {
        if (payload.Length < 16 + 12 + 16) throw new Exception("Payload too short for encrypted format.");
        int pos = 0;
        byte[] salt = new byte[16]; Array.Copy(payload, pos, salt, 0, 16); pos += 16;
        byte[] nonce = new byte[12]; Array.Copy(payload, pos, nonce, 0, 12); pos += 12;
        byte[] tag = new byte[16]; Array.Copy(payload, pos, tag, 0, 16); pos += 16;
        byte[] cipher = new byte[payload.Length - pos]; Array.Copy(payload, pos, cipher, 0, cipher.Length);

        using var kdf = new Rfc2898DeriveBytes(password ?? string.Empty, salt, 200000, HashAlgorithmName.SHA256);
        var key = kdf.GetBytes(32);

        byte[] plain = new byte[cipher.Length];
        using (var aesg = new AesGcm(key))
        {
            aesg.Decrypt(nonce, cipher, tag, plain);
        }
        return plain;
    }
}
