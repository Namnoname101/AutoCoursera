# CustomerManager — bài Peer-graded nộp PDF

Bản cập nhật dành cho `D:\Acose\CustomerManager\Acose.CustomerManager.exe`.

Khi chạy khóa, bộ auto mở **My submission**, tìm PDF soạn sẵn theo đúng khóa và tên bài, bấm **Add File** và nộp khi biểu mẫu sẵn sàng. Bài đã nộp sẽ bỏ qua upload. Nếu bài đang chờ chấm hoặc chưa đủ lượt review, công cụ dừng với trạng thái cần xử lý; không coi đó là hoàn thành toàn bộ khóa. Phần **Review your peers** vẫn là bước riêng.

Bản sửa hộp upload: nếu **Add File** mở hộp **Upload File**, bộ auto xử lý **browse files** hoặc input ẩn trong hộp, bấm **Upload 1 file** khi cần, chờ upload hoàn tất rồi mới nộp. Tiêu đề/tên dự án trống được tự điền bằng tên bài hoặc `okkkook`; giữ nguyên nội dung đã nhập và không thay đổi tên tài khoản.

## Đặt PDF ở đâu?

Thư mục chính là `D:\Acose\CustomerManager\PreparedPdfs`. Tạo một thư mục con theo slug khóa học (phần sau `/learn/` trong link), rồi đặt tên PDF theo tên bài hoặc slug bài:

```text
PreparedPdfs\
  uva-darden-experiencing-design\
    Personal Development Plan (PDP).pdf
```

Ví dụ trên dùng cho bài `/learn/uva-darden-experiencing-design/peer/KZ69l/personal-development-plan-pdp`. Tên `personal-development-plan-pdp.pdf` hoặc `Graded Assignment Personal Development Plan (PDP).pdf` cũng phù hợp.

Bộ auto chỉ tìm trong thư mục của đúng khóa tại các nơi sau:

1. Thư mục đặt trong biến môi trường `ACOSE_PREPARED_PDF_ROOT`, nếu có.
2. `D:\Acose\CustomerManager\PreparedPdfs`.
3. `D:\Acose\CustomerManager\Engine\PreparedPdfs`.
4. `C:\Users\dai99\Downloads\Acoursera PDFs` (thư mục Downloads của tài khoản Windows đang chạy).

Mỗi nơi đều phải có thư mục con theo khóa. PDF đang nằm trong `Downloads\Acoursera PDFs\<slug-khóa>` tiếp tục dùng được. Nếu không tìm thấy PDF, tên không khớp hoặc có nhiều file cùng khớp (kể cả ở nhiều nơi trên), khóa dừng và hiện lý do để bạn bổ sung/chọn lại file rồi chạy lại. Chỉ giữ một PDF khớp mỗi bài trong các thư mục kho. Nội dung PDF do bạn chuẩn bị sẵn.

## Cài bản cập nhật

Đóng CustomerManager và các cửa sổ khóa/kiểm tra của nó. Sau khi Release build và các kiểm tra đã đạt, chạy `deploy.ps1 -VerificationSummary "<kết quả kiểm tra thực tế>"` trong thư mục bản cập nhật. Script từ chối cập nhật khi công cụ còn chạy hoặc bộ auto đã khác phiên bản nền được kiểm tra.

Script chỉ thay DLL/PDB của bộ auto, cập nhật manifest, tạo `PreparedPdfs` nếu chưa có và lưu nguồn/kiểm tra tại `Updates\peer-pdf-...`. `Before` chứa DLL/PDB và manifest trước cập nhật; nếu thay file thất bại, script khôi phục chúng. Dữ liệu đơn, tiến độ, profile đăng nhập và cấu hình mã hóa không bị sửa; các PDF hiện có không bị di chuyển hay ghi đè. Thông tin cập nhật đăng ký khóa (`EnrollmentUpdate`) được giữ lại.

Mở lại CustomerManager để chạy bản mới. Không cần đổi đường dẫn Engine hoặc thiết lập lại tài khoản.

## Kiểm tra bản cập nhật

- Release `net10.0-windows/win-x64`: build thành công, 0 lỗi.
- `Checks`: 116 kiểm tra PDF, hộp Upload File/browse files, title, upload CDP qua Chromium, xác nhận nộp và không nộp trùng; bao gồm file input và folder input Uppy cùng tồn tại.
- `ScannerChecks`: 26 kiểm tra scanner, Resume/Results pending, module xanh, bài đã xác nhận đạt và giữ Review thủ công.
- `EnrollmentChecks`: 27 kiểm tra luồng đăng ký, giữ nguyên bản sửa FPT/organization và chặn free trial/paid.
- Các kiểm tra trình duyệt chạy trên trang mô phỏng cục bộ; chưa nộp thật trên tài khoản khách.
- Lần xác minh cuối chạy với Chrome (`ACOSE_TEST_BROWSER=chrome`); Edge đã chạy bộ PDF đạt trước đó nhưng lần khởi động lại trình duyệt test gặp `TargetClosedException` trước khi mở trang. Cấu hình test có thể chọn Edge hoặc Chrome, không thay đổi WebView2 của Worker.
- Build còn cảnh báo đã có trong phiên bản nền (nullable/biến không dùng và gói SQLitePCLRaw.lib.e_sqlite3 2.1.10). Bản này không nâng cấp các dependency.

Mã xử lý mới: `MainWindow.PeerPdf.cs`, `Services/PeerPdfProbe.js`, `Services/PreparedPdfLibrary.cs`. Hàm `HandlePdfFormSubmissionAsync` gọi vào `HandlePreparedPeerPdfAsync`.

## Sửa dừng trước khi chọn PDF (2026-09-16)

Đã kiểm tra trực tiếp DOM bài Insights and Design Criteria ở chế độ Mở kiểm tra, không chọn/upload hoặc nộp file. Uppy tạo hai input ẩn có cùng class `uppy-Dashboard-input`, trong đó một input có `webkitdirectory` dùng để chọn thư mục. Bộ chọn cũ đếm cả hai và báo `AMBIGUOUS_UPLOAD`. Bộ chọn mới loại input chọn thư mục, chỉ gắn PDF vào input chọn file; hai input chọn file thật vẫn bị chặn để tránh chọn nhầm.

Khi bài bị tạm dừng, Manager vẫn dùng cơ chế đóng Worker để nhả profile. Thông báo dừng PDF hiện giữ lại lỗi/trạng thái cụ thể cùng hướng dẫn, thay vì chỉ còn câu “Kiểm tra My submission rồi chạy lại khóa”. Không coi lỗi upload hoặc trạng thái chờ chấm là đã hoàn thành.
