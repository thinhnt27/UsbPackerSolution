using System;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Policy;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using VideoPackerSolution.Utils;
using MessageBox = System.Windows.MessageBox;
using Microsoft.Win32;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog; // ở đầu file nếu chưa có

namespace UsbPacker
{
    public partial class MainWindow : Window
    {
        public class DriveEntry
        {
            public string Root { get; set; } = "";      // e.g. "E:\"
            public string VolumeLabel { get; set; } = "";
            public string Display => $"{Root} ({VolumeLabel})";
            public bool IsChecked { get; set; } = false;
        }

        private ObservableCollection<DriveEntry> DrivesCollection = new ObservableCollection<DriveEntry>();
        private System.Collections.Generic.List<string> LoadedHashes = new System.Collections.Generic.List<string>();

        public MainWindow()
        {
            InitializeComponent();
            DrivesListBox.ItemsSource = DrivesCollection;
            RefreshDrives();
        }

        private void RefreshDrivesBtn_Click(object sender, RoutedEventArgs e) => RefreshDrives();

        private void RefreshDrives()
        {
            DrivesCollection.Clear();
            var drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Removable && d.IsReady)
                .ToArray();
            foreach (var d in drives)
            {
                var root = d.Name;
                if (!root.EndsWith("\\")) root += "\\";
                DrivesCollection.Add(new DriveEntry { Root = root, VolumeLabel = d.VolumeLabel ?? "" });
            }
            StatusText.Text = $"Found {DrivesCollection.Count} removable drive(s).";
        }

        private void SelectAllDrivesBtn_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in DrivesCollection) item.IsChecked = true;
            DrivesListBox.Items.Refresh();
        }

        private void DeselectAllDrivesBtn_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in DrivesCollection) item.IsChecked = false;
            DrivesListBox.Items.Refresh();
        }

        // Append selected drives -> compute hashes and add to LoadedHashes
        private void AddSelectedUsbHashesBtn_Click(object sender, RoutedEventArgs e)
        {
            var selected = DrivesCollection.Where(x => x.IsChecked).ToArray();
            if (selected.Length == 0)
            {
                MessageBox.Show("Chưa chọn USB nào. Hãy tick ít nhất 1 ổ.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int added = 0;
            foreach (var d in selected)
            {
                try
                {
                    if (!UsbAuth.TryGetHardwareSerialFromDriveRoot(d.Root, out var hwSerial, out var cap, out var phys, out var dbg))
                    {
                        MessageBox.Show($"Không lấy được Hardware Serial cho {d.Root}.\n{dbg}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        continue;
                    }

                    string hash = UsbAuth.MakeSaltedHash(UsbAuth.DefaultSalt, hwSerial);
                    if (!LoadedHashes.Any(h => string.Equals(h, hash, StringComparison.OrdinalIgnoreCase)))
                    {
                        LoadedHashes.Add(hash);
                        added++;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Không lấy được serial cho {d.Root}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            UpdateHashesCount();
            StatusText.Text = $"Added {added} new hash(es) from selected drives.";
        }

        // REPLACE selected drives -> replace LoadedHashes with only selected drives' hashes
        private void RefreshSelectedUsbHashesBtn_Click(object sender, RoutedEventArgs e)
        {
            var selected = DrivesCollection.Where(x => x.IsChecked).ToArray();
            if (selected.Length == 0)
            {
                MessageBox.Show("Chưa chọn USB nào. Hãy tick ít nhất 1 ổ.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var newList = new System.Collections.Generic.List<string>();
            int added = 0;
            foreach (var d in selected)
            {
                try
                {
                    if (!UsbAuth.TryGetHardwareSerialFromDriveRoot(d.Root, out var hwSerial, out var cap, out var phys, out var dbg))
                    {
                        MessageBox.Show($"Không lấy được Hardware Serial cho {d.Root}.\n{dbg}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        continue;
                    }

                    string hash = UsbAuth.MakeSaltedHash(UsbAuth.DefaultSalt, hwSerial);
                    if (!newList.Any(h => string.Equals(h, hash, StringComparison.OrdinalIgnoreCase)))
                    {
                        newList.Add(hash);
                        added++;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Không lấy được serial cho {d.Root}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            LoadedHashes = newList;
            UpdateHashesCount();
            StatusText.Text = $"Replaced hashes: now {LoadedHashes.Count} hash(es) (from selected drives).";
        }

        private void UpdateHashesCount()
        {
            HashesCountText.Text = $"Hashes: {LoadedHashes.Count}";
        }

        // files UI
        private void AddFilesBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.*" };
            if (dlg.ShowDialog() == true)
                foreach (var f in dlg.FileNames) FilesList.Items.Add(f);
        }

        private void RemoveBtn_Click(object sender, RoutedEventArgs e)
        {
            var sel = FilesList.SelectedItems.Cast<object>().ToArray();
            foreach (var s in sel) FilesList.Items.Remove(s);
        }

        // pack start
        private async void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (FilesList.Items.Count == 0) { MessageBox.Show("Chưa có file nào để pack."); return; }

            // ensure we have hashes (must come from selected USBs)
            if (LoadedHashes == null || LoadedHashes.Count == 0)
            {
                var res = MessageBox.Show("Bạn chưa thêm hash nào từ USB. Muốn tiếp tục mà không nhúng hash? (không nhúng = file exe sẽ không cho mở trên USB nào)", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.No) return;
            }

            // find stub.exe in same folder as app
            var appFolder = AppDomain.CurrentDomain.BaseDirectory;
            var stubPath = Path.Combine(appFolder, "StubPlayer.exe");
            if (!File.Exists(stubPath))
            {
                MessageBox.Show($"Không tìm thấy StubPlayer.exe ở:  {stubPath} \nBạn phải copy StubPlayer.exe (player) vào cùng thư mục với chương trình này.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Done folder next to app
            var doneFolder = Path.Combine(appFolder, "Done");
            Directory.CreateDirectory(doneFolder);

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

                    // create zip for single file
                    byte[] zip = await Task.Run(() => CreateZipForSingleFile(file));

                    // output name
                    var baseName = Path.GetFileNameWithoutExtension(file);
                    var safeName = MakeSafeFileName(baseName);
                    var outName = safeName + ".exe";
                    var outPath = Path.Combine(doneFolder, outName);
                    int suffix = 1;
                    while (File.Exists(outPath))
                    {
                        outName = $"{safeName}_{suffix}.exe";
                        outPath = Path.Combine(doneFolder, outName);
                        suffix++;
                    }

                    // prepare hashes bytes if any
                    byte[] hashesBytes = Array.Empty<byte>();
                    if (LoadedHashes.Count > 0)
                    {
                        var json = JsonSerializer.Serialize(LoadedHashes, new JsonSerializerOptions { WriteIndented = true });
                        hashesBytes = Encoding.UTF8.GetBytes(json);
                    }

                    // append payload + metadata
                    StatusText.Text = $"({idx}/{total}) Ghi {outName} vào {doneFolder} ...";
                    await Task.Run(() => AppendPayloadAndEmbeddedHashes(stubPath, outPath, zip, hashesBytes));
                    StatusText.Text = $"({idx}/{total}) Hoàn tất: {outName}";
                }

                Progress.Value = 100;
                ProgressPercent.Text = "100%";
                MessageBox.Show($"Hoàn tất đóng gói tất cả file. Files are in:\n{doneFolder}", "OK", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi: " + ex.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private void ExportHashesBtn_Click(object sender, RoutedEventArgs e)
        {
            if (LoadedHashes == null || LoadedHashes.Count == 0)
            {
                MessageBox.Show("Không có hash nào để xuất.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                FileName = "hashes.json",
                DefaultExt = ".json",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(LoadedHashes, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    System.IO.File.WriteAllText(dlg.FileName, json, System.Text.Encoding.UTF8);
                    MessageBox.Show($"Đã xuất {LoadedHashes.Count} hash vào:\n{dlg.FileName}", "Exported", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Lỗi khi lưu file: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        // helpers
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

        static void AppendPayloadAndEmbeddedHashes(string stubPath, string outPath, byte[] payload, byte[] hashesJsonBytes)
        {
            File.Copy(stubPath, outPath, overwrite: true);
            using var outFs = File.Open(outPath, FileMode.Append, FileAccess.Write);

            // payload
            outFs.Write(payload, 0, payload.Length);

            // payload length
            outFs.Write(BitConverter.GetBytes((long)payload.Length), 0, 8);

            // payload magic
            var payloadMagic = Encoding.ASCII.GetBytes("VIDPKG1\0");
            outFs.Write(payloadMagic, 0, payloadMagic.Length);

            // if no hashes, done
            if (hashesJsonBytes == null || hashesJsonBytes.Length == 0) return;

            // metadata marker
            var metaMarker = Encoding.ASCII.GetBytes("HASHPKG1");
            outFs.Write(metaMarker, 0, metaMarker.Length);

            // write hashes length and bytes
            outFs.Write(BitConverter.GetBytes((long)hashesJsonBytes.Length), 0, 8);
            outFs.Write(hashesJsonBytes, 0, hashesJsonBytes.Length);
        }
    }
}
// Nút 'Add File' để thêm video muốn add vô USB
// Nút 'Remove selected' để xóa video không muốn add trong Files to pack
// 
// Mode có 2 chế độ: USB và Json
//  USB:
//      Nút 'Refresh': làm mới lại USB đã cắm
//      Nút 'Select all': chọn tất cả USB đang có
//      Nút 'Deselected': bỏ chọn tất cả USB đang chọn
//      Nút 'Add -> Hashes': hash cái usb đang chọn thành json lưu trữ trong code
//      Nút 'Refresh -> Hashes': để làm mới json(khi có thêm Usb mới) lại khi đã 'Add -> hashes' rồi
//      Nút 'Export hashes' để tiến hành lấy json đã hashes thành file json
//  Json:
//      Nút 'Import hashes': đưa json đã hashes cho các USB vào
//'Create EXE(s) -> Done': xuất hiện khi có ít nhất 1 json có trong chương trình; Để tiến hành đóng gói các video
//# Publish StubPlayer (player) → output ở .\publish\stub\StubPlayer.exe
//dotnet publish./ StubPlayer / StubPlayer.csproj - c Release - r win - x64 - p:PublishSingleFile = true - p:SelfContained = true - o./ publish / stub
//# Publish UsbPacker (UI) → output ở .\publish\usbpacker\UsbPacker.exe
//dotnet publish./ UsbPacker / UsbPacker.csproj - c Release - r win - x64 - p:PublishSingleFile = true - p:SelfContained = true - o./ publish / usbpacker
//# (Tuỳ chọn) Publish Packer CLI
//dotnet publish./ Packer / Packer.csproj - c Release - r win - x64 - p:PublishSingleFile = true - p:SelfContained = true - o./ publish / packer
//Sau khi các exe được release thì trong folder 'stub'; copy hết bỏ vô folder 'usbpacker'