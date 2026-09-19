# CustomerManager — tự đăng ký khóa trong đơn

Áp dụng tại `D:\Acose\CustomerManager\Acose.CustomerManager.exe`.

Bấm **Chạy** ở một khóa hoặc **Chạy cả đơn**. Khóa chưa đăng ký sẽ được mở bằng profile của đơn, bấm Enroll, xử lý xác nhận “Enroll through my organization” và tiếp tục vào nội dung học. Khóa đã đăng ký dùng lối vào khóa hiện có.

Nếu Coursera yêu cầu thanh toán, dùng thử, đăng nhập hoặc một phương án chưa nhận diện, khóa sẽ dừng và hiển thị lý do. Không báo “xong auto” chỉ vì đã bấm đăng ký. Các bước chờ và thử lại đều có giới hạn.

Kiểm tra ngày 15/09/2026:

- Release build thành công, 0 lỗi; vẫn có cảnh báo đã tồn tại trong nguồn cũ.
- 27 kiểm tra trình duyệt: xác nhận của tổ chức, modal cùng trang, chuyển chương trình rồi Enroll tiếp, chặn bấm lặp khi React render lại, khóa đã đăng ký, nút trùng, đường dẫn khác khóa, nút ẩn, Trial/thanh toán.
- Kiểm tra thật bằng CustomerManager với đơn TRƯƠNG THIỀU LAM, khóa Ideas to Action: tự đăng ký và vào Module 1/5; đã dừng lượt kiểm tra bằng nút Dừng.
- Discovery Tools cũng đã đăng ký khi đối chiếu hộp xác nhận bằng tay.

`Before` chứa DLL/PDB và manifest trước cập nhật. `Source` chứa nguồn bộ chạy, `Checks` chứa kiểm tra tự động. Chạy lại kiểm tra: `dotnet run --project Checks/EnrollmentChecks.csproj -c Release` (máy cần Microsoft Edge).

Chỉ cập nhật DLL/PDB của bộ chạy và thông tin bản cập nhật; dữ liệu đơn và profile do CustomerManager tiếp tục quản lý.
