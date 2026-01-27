// File: MainWindow.xaml.cs
using Microsoft.Win32;
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
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

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
        private bool ImportedFromJson = false;

        public MainWindow()
        {
            InitializeComponent();

            // Ensure controls exist before using them (guard)
            if (this.FindName("DrivesListBox") is System.Windows.Controls.ListBox lb)
                lb.ItemsSource = DrivesCollection;

            // Default mode: Use USB
            if (this.FindName("UseUsbRadio") is System.Windows.Controls.RadioButton rbUsb)
                rbUsb.IsChecked = true;

            // Set UI mode safely (will check for panels existence)
            SetModeUI(useJson: false);

            RefreshDrives();
            UpdateHashesCount();
        }

        // Mode radio checked handler (wired in XAML)
        private void ModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            var useJsonRadio = this.FindName("UseJsonRadio") as System.Windows.Controls.RadioButton;
            var useUsbRadio = this.FindName("UseUsbRadio") as System.Windows.Controls.RadioButton;

            bool useJson = useJsonRadio != null && useJsonRadio.IsChecked == true;

            // if there are existing hashes, confirm clearing before switching mode
            if (LoadedHashes != null && LoadedHashes.Count > 0)
            {
                var res = MessageBox.Show("Bạn đang chuyển chế độ. Việc chuyển sẽ xoá danh sách hash hiện tại. Tiếp tục?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res == MessageBoxResult.No)
                {
                    // revert selection if user cancels
                    if (useJsonRadio != null && useUsbRadio != null)
                    {
                        if (useJson) { useJsonRadio.IsChecked = false; useUsbRadio.IsChecked = true; }
                        else { useUsbRadio.IsChecked = false; useJsonRadio.IsChecked = true; }
                    }
                    return;
                }
                LoadedHashes.Clear();
                ImportedFromJson = false;
                UpdateHashesCount();
            }

            SetModeUI(useJson);
        }

        private void SetModeUI(bool useJson)
        {
            var jsonPanel = this.FindName("JsonControlsPanel") as UIElement;
            var usbPanel = this.FindName("UsbControlsPanel") as UIElement;
            var drivesList = this.FindName("DrivesListBox") as System.Windows.Controls.ListBox;

            if (jsonPanel != null) jsonPanel.Visibility = useJson ? Visibility.Visible : Visibility.Collapsed;
            if (usbPanel != null) usbPanel.Visibility = useJson ? Visibility.Collapsed : Visibility.Visible;
            if (drivesList != null) drivesList.IsEnabled = !useJson;
        }

        private void RefreshDrivesBtn_Click(object sender, RoutedEventArgs e) => RefreshDrives();

        private void RefreshDrives()
        {
            LoadedHashes = new List<string>();
            UpdateHashesCount();
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
            SafeSetStatus($"Found {DrivesCollection.Count} removable drive(s).");
        }

        private void SelectAllDrivesBtn_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in DrivesCollection) item.IsChecked = true;
            (this.FindName("DrivesListBox") as System.Windows.Controls.ListBox)?.Items.Refresh();
        }

        private void DeselectAllDrivesBtn_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in DrivesCollection) item.IsChecked = false;
            (this.FindName("DrivesListBox") as System.Windows.Controls.ListBox)?.Items.Refresh();
            LoadedHashes = new List<string>();
            UpdateHashesCount();
        }

        // Add (append) from selected USBs
        private void AddSelectedUsbHashesBtn_Click(object sender, RoutedEventArgs e)
        {
            // Ensure in USB mode
            var useJsonRadio = this.FindName("UseJsonRadio") as System.Windows.Controls.RadioButton;
            if (useJsonRadio != null && useJsonRadio.IsChecked == true)
            {
                MessageBox.Show("Đang ở chế độ Import JSON — để dùng USB hãy chuyển sang 'Use selected USBs'.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var selected = DrivesCollection.Where(x => x.IsChecked).ToArray();
            if (selected.Length == 0)
            {
                MessageBox.Show("Chưa chọn USB nào. Hãy tick ít nhất 1 ổ.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (ImportedFromJson)
            {
                var resConfirm = MessageBox.Show("Bạn đã import hashes từ file JSON. Thao tác 'Add → hashes' sẽ **xoá** danh sách hiện tại và thêm hash từ USB đã chọn. Tiếp tục?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (resConfirm == MessageBoxResult.No) return;
                LoadedHashes.Clear();
                ImportedFromJson = false;
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
            SafeSetStatus($"Added {added} new hash(es) from selected drives.");
        }

        // Replace (overwrite) from selected USBs
        private void RefreshSelectedUsbHashesBtn_Click(object sender, RoutedEventArgs e)
        {
            var useJsonRadio = this.FindName("UseJsonRadio") as System.Windows.Controls.RadioButton;
            if (useJsonRadio != null && useJsonRadio.IsChecked == true)
            {
                MessageBox.Show("Đang ở chế độ Import JSON — để dùng USB hãy chuyển sang 'Use selected USBs'.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            
            var selected = DrivesCollection.Where(x => x.IsChecked).ToArray();
            if (selected.Length == 0)
            {
                MessageBox.Show("Chưa chọn USB nào. Hãy tick ít nhất 1 ổ.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var newList = new List<string>();
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
            ImportedFromJson = false;
            UpdateHashesCount();
            SafeSetStatus($"Replaced hashes: now {LoadedHashes.Count} hash(es) (from selected drives).");
        }

        // IMPORT hashes from JSON file
        private void ImportHashesBtn_Click(object sender, RoutedEventArgs e)
        {
            var useUsbRadio = this.FindName("UseUsbRadio") as System.Windows.Controls.RadioButton;
            if (useUsbRadio != null && useUsbRadio.IsChecked == true)
            {
                MessageBox.Show("Đang ở chế độ USB — chuyển sang 'Import JSON' để import file.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var ofd = new OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                Title = "Select hashes JSON file"
            };

            if (ofd.ShowDialog() == true)
            {
                try
                {
                    var txt = File.ReadAllText(ofd.FileName, Encoding.UTF8);
                    if (!TryParseHashesFromText(txt, out var hashes, out var err))
                    {
                        MessageBox.Show($"Không thể parse JSON: {err}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    LoadedHashes = hashes;
                    ImportedFromJson = true;
                    UpdateHashesCount();
                    SafeSetStatus($"Imported {LoadedHashes.Count} hash(es) from file: {Path.GetFileName(ofd.FileName)}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Lỗi đọc file: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // Export
        //private void ExportHashesBtn_Click(object sender, RoutedEventArgs e)
        //{
        //    // ensure we're in USB mode
        //    var useJsonRadio = this.FindName("UseJsonRadio") as System.Windows.Controls.RadioButton;
        //    if (useJsonRadio != null && useJsonRadio.IsChecked == true)
        //    {
        //        MessageBox.Show("Export chỉ khả dụng khi đang ở chế độ 'Use selected USBs'. Hãy chuyển chế độ về USB.", "Không hợp lệ", MessageBoxButton.OK, MessageBoxImage.Information);
        //        return;
        //    }

        //    if (LoadedHashes == null || LoadedHashes.Count == 0)
        //    {
        //        MessageBox.Show("Không có hash nào để xuất.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        //        return;
        //    }

        //    var dlg = new SaveFileDialog
        //    {
        //        FileName = "hashes.json",
        //        DefaultExt = ".json",
        //        Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
        //    };

        //    if (dlg.ShowDialog() == true)
        //    {
        //        try
        //        {
        //            var json = JsonSerializer.Serialize(LoadedHashes, new JsonSerializerOptions { WriteIndented = true });
        //            File.WriteAllText(dlg.FileName, json, Encoding.UTF8);
        //            MessageBox.Show($"Đã xuất {LoadedHashes.Count} hash vào:\n{dlg.FileName}", "Exported", MessageBoxButton.OK, MessageBoxImage.Information);
        //        }
        //        catch (Exception ex)
        //        {
        //            MessageBox.Show("Lỗi khi lưu file: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        //        }
        //    }
        //}
        private void ExportHashesBtn_Click(object sender, RoutedEventArgs e)
        {
            // ensure we're in USB mode
            var useJsonRadio = this.FindName("UseJsonRadio") as System.Windows.Controls.RadioButton;
            if (useJsonRadio != null && useJsonRadio.IsChecked == true)
            {
                MessageBox.Show(
                    "Export chỉ khả dụng khi đang ở chế độ 'Use selected USBs'. Hãy chuyển chế độ về USB.",
                    "Không hợp lệ",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
                return;
            }

            if (LoadedHashes == null || LoadedHashes.Count == 0)
            {
                MessageBox.Show(
                    "Không có hash nào để xuất.",
                    "Info",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
                return;
            }

            // 🔹 LẤY SUBJECT (đổi theo app của bạn)
            var subject = SubjectNameBox.Text;

            if (string.IsNullOrWhiteSpace(subject))
            {
                MessageBox.Show(
                    "Vui lòng nhập Subject trước khi export.",
                    "Thiếu thông tin",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            // 🔹 BASE FOLDER (cùng thư mục exe)
            string baseDir = AppContext.BaseDirectory;
            string hashesDir = Path.Combine(baseDir, "Hashes", subject);

            try
            {
                Directory.CreateDirectory(hashesDir);

                string outputPath = Path.Combine(hashesDir, "hashes.json");

                var json = JsonSerializer.Serialize(
                    LoadedHashes,
                    new JsonSerializerOptions { WriteIndented = true }
                );

                File.WriteAllText(outputPath, json, Encoding.UTF8);

                MessageBox.Show(
                    $"Đã xuất {LoadedHashes.Count} hash vào:\n{outputPath}",
                    "Exported",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Lỗi khi lưu file: " + ex.Message,
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }


        // files UI
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

        // Start pack
        private async void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SubjectNameBox.Text))
            {
                MessageBox.Show("Bạn phải nhập Subject name trước khi đóng gói!", "Thiếu thông tin",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (FilesList.Items.Count == 0) { MessageBox.Show("Chưa có file nào để pack."); return; }

            var appFolder = AppDomain.CurrentDomain.BaseDirectory;
            var stubPath = Path.Combine(appFolder, "StubPlayer.exe");
            if (!File.Exists(stubPath))
            {
                MessageBox.Show($"Không tìm thấy StubPlayer.exe ở: {stubPath}\nBạn phải copy StubPlayer.exe (player) vào cùng thư mục với chương trình này.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            //var doneFolder = Path.Combine(appFolder, "Done");
            //Directory.CreateDirectory(doneFolder);
            var subjectNameFolder = SubjectNameBox.Text.Trim();
            var safeSubject = MakeSafeFileName(subjectNameFolder);

            var doneFolder = Path.Combine(appFolder, "Done", safeSubject);
            Directory.CreateDirectory(doneFolder);

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
                    SafeSetStatus($"({idx}/{total}) Đang xử lý: {Path.GetFileName(file)}");

                    byte[] zip = await Task.Run(() => CreateZipForSingleFile(file));

                    var baseName = Path.GetFileNameWithoutExtension(file);
                    var safeName = MakeSafeFileName(baseName);
                    var outName = safeName + ".exe";
                    var outPath = Path.Combine(doneFolder, outName);
                    //int suffix = 1;
                    //while (File.Exists(outPath))
                    //{
                    //    outName = $"{safeName}_{suffix}.exe";
                    //    outPath = Path.Combine(doneFolder, outName);
                    //    suffix++;
                    //}

                    byte[] hashesBytes = Array.Empty<byte>();
                    if (LoadedHashes.Count > 0)
                    {
                        var json = JsonSerializer.Serialize(LoadedHashes, new JsonSerializerOptions { WriteIndented = true });
                        hashesBytes = Encoding.UTF8.GetBytes(json);
                    }

                    SafeSetStatus($"({idx}/{total}) Ghi {outName} vào {doneFolder} ...");
                    await Task.Run(() => AppendPayloadAndEmbeddedHashes(stubPath, outPath, zip, hashesBytes));
                    SafeSetStatus($"({idx}/{total}) Hoàn tất: {outName}");
                }
                // === Save to SQLite DB ===
                try
                {
                    string subjectName = SubjectNameBox.Text?.Trim();
                    if (string.IsNullOrEmpty(subjectName))
                        subjectName = "(no subject)";

                    if (LoadedHashes != null && LoadedHashes.Count > 0)
                    {
                        foreach (var hash in LoadedHashes)
                        {
                            LocalDatabase.InsertHash(hash, subjectName);
                        }
                        SafeSetStatus($"Đã lưu {LoadedHashes.Count} hash vào local.db (subject: {subjectName})");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Lỗi ghi SQLite: {ex.Message}", "DB Error", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                SafeSetStatus("Sẵn sàng");
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

            outFs.Write(payload, 0, payload.Length);
            outFs.Write(BitConverter.GetBytes((long)payload.Length), 0, 8);
            var payloadMagic = Encoding.ASCII.GetBytes("VIDPKG1\0");
            outFs.Write(payloadMagic, 0, payloadMagic.Length);

            if (hashesJsonBytes == null || hashesJsonBytes.Length == 0) return;

            var metaMarker = Encoding.ASCII.GetBytes("HASHPKG1");
            outFs.Write(metaMarker, 0, metaMarker.Length);
            outFs.Write(BitConverter.GetBytes((long)hashesJsonBytes.Length), 0, 8);
            outFs.Write(hashesJsonBytes, 0, hashesJsonBytes.Length);
        }

        private bool TryParseHashesFromText(string txt, out System.Collections.Generic.List<string> hashes, out string err)
        {
            hashes = new System.Collections.Generic.List<string>();
            err = null;
            if (string.IsNullOrWhiteSpace(txt)) return true;
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var tmp = JsonSerializer.Deserialize<System.Collections.Generic.List<string>>(txt, opts) ?? new System.Collections.Generic.List<string>();
                var normalized = tmp.Select(h => h?.Trim()?.ToLowerInvariant()).Where(h => !string.IsNullOrEmpty(h)).ToList();
                foreach (var h in normalized)
                {
                    if (h.Length != 64)
                    {
                        err = $"Hash '{h}' không hợp lệ (không đủ 64 ký tự hex).";
                        return false;
                    }
                    if (!System.Text.RegularExpressions.Regex.IsMatch(h, @"\A\b[0-9a-f]{64}\b\Z"))
                    {
                        err = $"Hash '{h}' chứa ký tự không hợp lệ (phải là hex).";
                        return false;
                    }
                }
                hashes = normalized.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return false;
            }
        }

        private void UpdateHashesCount()
        {
            // cập nhật text hiển thị
            var countText = this.FindName("HashesCountText") as System.Windows.Controls.TextBlock;
            if (countText != null) countText.Text = $"{LoadedHashes.Count}";

            // tìm Start button (an toàn nếu tên control bị đổi)
            var startBtn = this.FindName("StartBtn") as System.Windows.Controls.Button;
            if (startBtn != null)
            {
                bool enabled = LoadedHashes != null && LoadedHashes.Count > 0;

                // Nếu bạn muốn thêm điều kiện khác (ví dụ phải có file trong FilesList),
                // thay `enabled` = enabled && FilesList.Items.Count > 0

                startBtn.IsEnabled = enabled;
                  
                // thay đổi giao diện "nhạt" khi disabled
                // (opacity là cách nhanh, bạn có thể thay bằng đổi Background nếu muốn)
                startBtn.Opacity = enabled ? 1.0 : 0.45;

                // tooltip giải thích lý do disabled
                startBtn.ToolTip = enabled ? "Create EXE(s) into ./Done" : "Disabled: chưa có hash nào. Chọn USB hoặc Import JSON trước.";
            }
        }


        private void SafeSetStatus(string text)
        {
            var st = this.FindName("StatusText") as System.Windows.Controls.TextBlock;

            if (st != null) st.Text = text;
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

//dotnet publish ./Packer/Packer.csproj `
//  -c Release `
//  -r win-x64 `
//  -p:PublishSingleFile=true `
//  -p:SelfContained=true `
//  -o ./publish/packer
//dotnet publish ./UsbPacker/UsbPacker.csproj `
//  -c Release `
//  -r win-x64 `
//  -p:PublishSingleFile=true `
//  -p:SelfContained=true `
//  -o ./publish/usbpacker
//dotnet publish ./StubPlayer/StubPlayer.csproj `
//  -c Release `
//  -r win-x64 `
//  -p:PublishSingleFile=true `
//  -p:SelfContained=true `
//  -o ./publish/stub


