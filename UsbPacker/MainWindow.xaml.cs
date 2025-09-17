using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using VideoPackerSolution.Utils;
using WinForms = System.Windows.Forms;

namespace UsbPacker
{
    public partial class MainWindow : Window
    {
        private List<string> LoadedHashes = new List<string>();

        public MainWindow()
        {
            InitializeComponent();
            RefreshDrives();
        }

        private void RefreshDrivesBtn_Click(object sender, RoutedEventArgs e) => RefreshDrives();

        private void RefreshDrives()
        {
            DrivesCombo.Items.Clear();
            var drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Removable && d.IsReady)
                .ToArray();
            foreach (var d in drives) DrivesCombo.Items.Add($"{d.Name} ({d.VolumeLabel})");
            if (DrivesCombo.Items.Count > 0) DrivesCombo.SelectedIndex = 0;
        }

        private void AddFilesBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.*" };
            if (dlg.ShowDialog() == true)
            {
                foreach (var f in dlg.FileNames) FilesList.Items.Add(f);
            }
        }

        private void RemoveBtn_Click(object sender, RoutedEventArgs e)
        {
            var sel = FilesList.SelectedItems.Cast<object>().ToArray();
            foreach (var s in sel) FilesList.Items.Remove(s);
        }

        private void LoadHashesBtn_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "JSON files|*.json;*.txt|All files|*.*" };
            if (ofd.ShowDialog() == true)
            {
                try
                {
                    var txt = File.ReadAllText(ofd.FileName);
                    if (!TryParseHashesFromText(txt, out var hashes, out var err))
                    {
                        System.Windows.MessageBox.Show($"Không thể parse JSON: {err}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    LoadedHashes = hashes;
                    HashesTextBox.Text = JsonSerializer.Serialize(LoadedHashes, new JsonSerializerOptions { WriteIndented = true });
                    StatusText.Text = $"Loaded {LoadedHashes.Count} hashes from {Path.GetFileName(ofd.FileName)}";
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("Lỗi đọc file: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void PasteHashesBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsText()) { System.Windows.MessageBox.Show("Clipboard không có text."); return; }
                var txt = System.Windows.Clipboard.GetText();
                if (!TryParseHashesFromText(txt, out var hashes, out var err))
                {
                    System.Windows.MessageBox.Show($"Không thể parse JSON: {err}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                LoadedHashes = hashes;
                HashesTextBox.Text = JsonSerializer.Serialize(LoadedHashes, new JsonSerializerOptions { WriteIndented = true });
                StatusText.Text = $"Loaded {LoadedHashes.Count} hashes from clipboard.";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Lỗi: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearHashesBtn_Click(object sender, RoutedEventArgs e)
        {
            LoadedHashes.Clear();
            HashesTextBox.Text = "";
            StatusText.Text = "Loaded hashes cleared.";
        }

        private bool TryParseHashesFromText(string txt, out List<string> hashes, out string err)
        {
            hashes = new List<string>();
            err = null;
            if (string.IsNullOrWhiteSpace(txt)) return true;
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var tmp = JsonSerializer.Deserialize<List<string>>(txt, opts) ?? new List<string>();
                hashes = tmp.Select(h => h?.Trim()?.ToLowerInvariant()).Where(h => !string.IsNullOrEmpty(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }

        private async void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (FilesList.Items.Count == 0) { System.Windows.MessageBox.Show("Chưa có file nào để pack."); return; }

            string outputFolder;
            if (DrivesCombo.SelectedItem != null)
            {
                outputFolder = DrivesCombo.SelectedItem.ToString().Split(' ')[0];
            }
            else
            {
                using var fbd = new WinForms.FolderBrowserDialog();
                var res = fbd.ShowDialog();
                if (res == WinForms.DialogResult.OK) outputFolder = fbd.SelectedPath;
                else { System.Windows.MessageBox.Show("Bạn chưa chọn USB hoặc folder để ghi."); return; }
            }
            if (!outputFolder.EndsWith(Path.DirectorySeparatorChar.ToString())) outputFolder += Path.DirectorySeparatorChar;

            if (!TryParseHashesFromText(HashesTextBox.Text, out var parsed, out var errParse))
            {
                System.Windows.MessageBox.Show($"Hashes JSON không hợp lệ: {errParse}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            LoadedHashes = parsed;

            // If embedding enabled, ensure at least one hash is present
            if (EmbedHashesCheck.IsChecked == true && (LoadedHashes == null || LoadedHashes.Count == 0))
            {
                var res = System.Windows.MessageBox.Show("Bạn bật 'Embed hashes' nhưng danh sách hashes trống. Tiếp tục không nhúng?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.No) return;
            }

            // find stub.exe next to app
            var appFolder = AppDomain.CurrentDomain.BaseDirectory;
            var stubPath = Path.Combine(appFolder, "StubPlayer.exe");
            if (!File.Exists(stubPath))
            {
                System.Windows.MessageBox.Show($"Không tìm thấy StubPlayer.exe ở: {stubPath}\nBạn phải copy StubPlayer.exe (player) vào cùng thư mục với chương trình này.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // lock UI
            StartBtn.IsEnabled = false;
            AddFilesBtn.IsEnabled = false;
            RemoveBtn.IsEnabled = false;
            RefreshDrivesBtn.IsEnabled = false;

            var files = FilesList.Items.Cast<string>().ToArray();
            int total = files.Length;
            int idx = 0;

            try
            {
                foreach (var file in files)
                {
                    idx++;
                    Progress.Value = (double)(idx - 1) / total * 100;
                    ProgressPercent.Text = $"{(int)Progress.Value}%";
                    StatusText.Text = $"({idx}/{total}) Đang xử lý: {Path.GetFileName(file)}";

                    // create zip
                    byte[] zip = await Task.Run(() => CreateZipForSingleFile(file));

                    // determine out name
                    var baseName = Path.GetFileNameWithoutExtension(file);
                    var safeName = MakeSafeFileName(baseName);
                    var outName = safeName + ".exe";
                    var outPath = Path.Combine(outputFolder, outName);
                    int suffix = 1;
                    while (File.Exists(outPath))
                    {
                        outName = $"{safeName}_{suffix}.exe";
                        outPath = Path.Combine(outputFolder, outName);
                        suffix++;
                    }

                    // prepare hashes bytes if embedding
                    byte[] hashesBytes = Array.Empty<byte>();
                    if (EmbedHashesCheck.IsChecked == true && LoadedHashes.Count > 0)
                    {
                        var json = JsonSerializer.Serialize(LoadedHashes, new JsonSerializerOptions { WriteIndented = true });
                        hashesBytes = Encoding.UTF8.GetBytes(json);
                    }

                    // append payload + metadata marker + hashes
                    StatusText.Text = $"({idx}/{total}) Ghi {outName} ...";
                    await Task.Run(() => AppendPayloadAndEmbeddedHashes(stubPath, outPath, zip, hashesBytes));
                    StatusText.Text = $"({idx}/{total}) Hoàn tất: {outName}";
                }

                Progress.Value = 100;
                ProgressPercent.Text = "100%";
                System.Windows.MessageBox.Show("Hoàn tất đóng gói tất cả file.", "OK", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Lỗi: " + ex.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StartBtn.IsEnabled = true;
                AddFilesBtn.IsEnabled = true;
                RemoveBtn.IsEnabled = true;
                RefreshDrivesBtn.IsEnabled = true;
                Progress.Value = 0;
                ProgressPercent.Text = "0%";
                StatusText.Text = "Sẵn sàng";
            }
        }

        // Helpers
        static byte[] CreateZipForSingleFile(string file)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                if (File.Exists(file))
                {
                    var entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                    using var es = entry.Open();
                    using var fs = File.OpenRead(file);
                    fs.CopyTo(es);
                }
            }
            return ms.ToArray();
        }

        static string MakeSafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length > 100) name = name.Substring(0, 100);
            return name;
        }

        // Append payload + payload_len + VIDPKG1 + (optional) HASHPKG1 + hashes_len + hashes_json
        static void AppendPayloadAndEmbeddedHashes(string stubPath, string outPath, byte[] payload, byte[] hashesJsonBytes)
        {
            File.Copy(stubPath, outPath, overwrite: true);
            using var outFs = File.Open(outPath, FileMode.Append, FileAccess.Write);

            // write payload
            outFs.Write(payload, 0, payload.Length);

            // write payload length (8 bytes LE)
            outFs.Write(BitConverter.GetBytes((long)payload.Length), 0, 8);

            // write payload marker
            var payloadMagic = Encoding.ASCII.GetBytes("VIDPKG1\0");
            outFs.Write(payloadMagic, 0, payloadMagic.Length);

            // if no hashes, done
            if (hashesJsonBytes == null || hashesJsonBytes.Length == 0) return;

            // write metadata marker
            var metaMarker = Encoding.ASCII.GetBytes("HASHPKG1");
            outFs.Write(metaMarker, 0, metaMarker.Length);

            // write hashes length (8 bytes LE), then bytes
            outFs.Write(BitConverter.GetBytes((long)hashesJsonBytes.Length), 0, 8);
            outFs.Write(hashesJsonBytes, 0, hashesJsonBytes.Length);
        }
    }
}
