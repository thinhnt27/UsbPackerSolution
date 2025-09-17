// File: Program.cs  (Packer)
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

class Program
{
    const string Magic = "VIDPKG1\0"; // 8 bytes

    static void Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: packer <stub.exe> <out.exe> <video1> [video2 ...] [--password=xxx]");
            return;
        }

        var stub = args[0];
        var outExe = args[1];
        var files = args.Skip(2).Where(a => !a.StartsWith("--")).ToArray();
        var passArg = args.FirstOrDefault(a => a.StartsWith("--password="));
        string password = passArg != null ? passArg.Split('=')[1] : null;

        if (!File.Exists(stub)) { Console.WriteLine("Stub not found: " + stub); return; }

        Console.WriteLine("Creating zip in-memory...");
        var zip = CreateZipInMemory(files);
        byte[] payload;
        if (!string.IsNullOrEmpty(password))
        {
            Console.WriteLine("Encrypting payload with password...");
            payload = EncryptWithPassword(zip, password);
        }
        else
        {
            payload = zip;
        }

        Console.WriteLine("Copying stub and appending payload...");
        File.Copy(stub, outExe, overwrite: true);
        using var outFs = File.Open(outExe, FileMode.Append, FileAccess.Write);
        outFs.Write(payload, 0, payload.Length);

        var lenBytes = BitConverter.GetBytes((long)payload.Length);
        outFs.Write(lenBytes, 0, lenBytes.Length);

        var magicBytes = Encoding.ASCII.GetBytes(Magic);
        outFs.Write(magicBytes, 0, magicBytes.Length);

        Console.WriteLine($"Packed {files.Length} files into {outExe} (payload {payload.Length} bytes).");
    }

    static byte[] CreateZipInMemory(string[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var f in files)
            {
                if (!File.Exists(f)) continue;
                var entry = zip.CreateEntry(Path.GetFileName(f), CompressionLevel.Optimal);
                using var es = entry.Open();
                using var fs = File.OpenRead(f);
                fs.CopyTo(es);
            }
        }
        return ms.ToArray();
    }

    // Format: [salt(16)] [nonce(12)] [tag(16)] [ciphertext...]
    static byte[] EncryptWithPassword(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        using var kdf = new Rfc2898DeriveBytes(password ?? string.Empty, salt, 200_000, HashAlgorithmName.SHA256);
        var key = kdf.GetBytes(32);

        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];

        using (var aesg = new AesGcm(key))
        {
            aesg.Encrypt(nonce, plain, cipher, tag);
        }

        using var outMs = new MemoryStream();
        outMs.Write(salt, 0, salt.Length);
        outMs.Write(nonce, 0, nonce.Length);
        outMs.Write(tag, 0, tag.Length);
        outMs.Write(cipher, 0, cipher.Length);
        return outMs.ToArray();
    }
}
