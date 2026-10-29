# Nhận profile website trên cùng PC — bản cập nhật 1.1.1

Luồng này giữ cơ chế đăng nhập website: khách gửi yêu cầu tại cổng dịch vụ (ví dụ: `https://your-auth-portal.example.com/`), Worker Host xử lý đăng nhập trên PC và bàn giao profile WebView2 sau khi hoàn tất. Không chuyển phiên đăng nhập sang máy khác.

## Cách dùng

1. Chạy Worker Host đã cấu hình cho website trên PC này.
2. Mở CustomerManager. Trong **Thiết lập**, chọn thư mục WorkerProfiles của Host, ví dụ `D:\Acose\WorkerProfiles` (không nhập URL website).
3. Khi yêu cầu website hoàn tất và Worker đăng nhập đã thoát, Host chuyển profile vào `_ManagedAccounts` cùng tệp `.acose-website-profile.json`.
4. Manager nhận khách và các link đã chọn khi mở, bấm Làm mới hoặc sau khoảng 10 giây. Khách mới ở trạng thái sẵn sàng; Manager không tự chạy khóa học khi nhập khách.
5. Chọn khách và bấm Mở kiểm tra hoặc Chạy khi cần. Nếu Coursera đã hết phiên hoặc yêu cầu xác minh, cần đăng nhập lại.

Bản đang dùng tại `D:\Acose\CustomerManager` giữ đường dẫn dữ liệu `Data` tại chỗ, cấu hình hiện có, Engine WebView2 và kho PDF. Bản mã nguồn dành cho Setup dùng đường dẫn dữ liệu theo tài khoản Windows như hướng dẫn phát hành; không dùng Setup cũ để thay bản cập nhật cục bộ này.

## Bảo toàn dữ liệu

- Tên và link sửa/xóa tại Manager không bị ghi đè khi đồng bộ lại.
- Dùng mã thiết bị ổn định để ghép lần đăng nhập mới với khách cũ; không ghép theo tên.
- Không chuyển profile khi có cửa sổ kiểm tra, khóa đang chạy/chờ hoặc tiến trình Worker chưa thoát.
- Chỉ quét profile trực tiếp trong `_ManagedAccounts`; không nhận thư mục đang đăng nhập `_DirectLogin` hoặc hồ sơ công việc `_CourseJobs`.
- Bỏ qua marker lỗi, đường dẫn mạng và junction/symlink. Không đọc kho cookie khi nhập danh sách khách.
- Host giữ profile đã đăng nhập nếu bàn giao gặp lỗi. Cần kiểm tra cảnh báo Host khi profile chưa xuất hiện; không tự xóa profile đó để thử lại.
- Profile cũ trước bản cập nhật vẫn nhập được nếu có metadata tài khoản hợp lệ, nhưng không tự khôi phục các link đã chọn từ website nếu trước đây chưa ghi lại.

## Quyền riêng tư và giới hạn

Website vẫn tiếp nhận thông tin đăng nhập theo luồng hiện tại; cần thông báo rõ và được khách đồng ý. Không hiển thị mật khẩu tại Manager không có nghĩa hệ thống không nhận mật khẩu. Tệp bàn giao mới chỉ gồm tên, các link Coursera, mã yêu cầu/thiết bị/người dùng và thời điểm hoàn tất, không có mật khẩu, API key hoặc cookie.

Bản sửa này không triển khai backend/website mới, không tạo cơ chế đăng nhập liên máy, không đảm bảo phiên Coursera tồn tại vĩnh viễn. Kiểm thử dùng dữ liệu giả, không gửi thông tin đăng nhập hay chạy khóa học thật.
