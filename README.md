# USB Video Packer

Công cụ dùng để **đóng gói video theo USB hoặc JSON hash**, sau đó **tạo EXE player riêng cho từng USB** nhằm kiểm soát nội dung phát.

---

## 1. Chức năng chính

### 1.1 Quản lý file video
- **Add File**  
  Thêm video muốn đóng gói vào danh sách **Files to pack**
- **Remove selected**  
  Xóa video đã chọn khỏi danh sách

---

## 2. Chế độ hoạt động (Mode)

Ứng dụng có **2 chế độ**: **USB** và **JSON**

---

### 2.1 USB Mode

Dùng khi muốn hash trực tiếp từ USB đang cắm.

**Các nút chức năng:**

- **Refresh**  
  Làm mới danh sách USB đang cắm vào máy

- **Select all**  
  Chọn tất cả USB đang hiển thị

- **Deselect all**  
  Bỏ chọn toàn bộ USB

- **Add → Hashes**  
  Hash các USB đang chọn → lưu thông tin hash vào bộ nhớ chương trình (dạng JSON)

- **Replace → Hashes**  
  Làm mới lại dữ liệu hash  
  _(Dùng khi đã Add → Hashes nhưng có thêm USB mới được cắm)_

- **Export hashes**  
  Xuất toàn bộ hash USB thành **file JSON**

---

### 2.2 JSON Mode

Dùng khi đã có sẵn file hash USB.

- **Import hashes**  
  Import file JSON chứa hash của các USB vào chương trình

---

## 3. Đóng gói EXE

### Create EXE(s) → Done

- Nút này **chỉ xuất hiện khi có ít nhất 1 JSON hash**
- Khi thực hiện:
  - Đóng gói video
  - Sinh các file **EXE player tương ứng với USB** ở folder Done

---

## 4. Publish các project

### 4.1 Publish StubPlayer (Player)

**Output**
```
.\publish\stub\StubPlayer.exe
```

**Command**
```bash
dotnet publish ./StubPlayer/StubPlayer.csproj \
  -c Release \
  -r win-x64 \
  -p:PublishSingleFile=true \
  -p:SelfContained=true \
  -o ./publish/stub
```

---

### 4.2 Publish UsbPacker (UI)

**Output**
```
.\publish\usbpacker\UsbPacker.exe
```

**Command**
```bash
dotnet publish ./UsbPacker/UsbPacker.csproj `
  -c Release `
  -r win-x64 `
  -p:PublishSingleFile=true `
  -p:SelfContained=true `
  -o ./publish/usbpacker

```

---

### 4.3 (Tuỳ chọn) Publish Packer CLI

**Output**
```
.\publish\packer\Packer.exe
```

**Command**
```bash
dotnet publish ./Packer/Packer.csproj `
  -c Release `
  -r win-x64 `
  -p:PublishSingleFile=true `
  -p:SelfContained=true `
  -o ./publish/packer

```

---

## 5. Cấu trúc sau khi publish

1. Vào thư mục:
   ```
   .\publish\stub\
   ```
2. Copy toàn bộ file trong thư mục này  
3. Dán vào:
   ```
   .\publish\usbpacker\
   ```

`UsbPacker.exe` sẽ sử dụng `StubPlayer.exe` để tạo player tương ứng.

---

## 6. Ghi chú

- Mỗi USB được xác định bằng **hash riêng**
- EXE chỉ hoạt động với USB đã được hash
- Workflow hỗ trợ:
  - Hash USB → Xuất JSON
  - Import JSON → Đóng gói video → Sinh EXE
