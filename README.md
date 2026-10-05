# Garena Orchestrator

Một image, hai chế độ chạy:

- `master`: admin dashboard, quản lý vệ tinh, proxy và IP outbound.
- `satellite`: kết nối tới master, báo IP public của VPS và kiểm tra proxy được gán.

Repo này là lớp điều phối. Nó **không chạy engine WinForms/WebView2 trên Linux**. Khi satellite chạy Windows, có thể gắn `GarenaRegisterEngine` hiện tại làm executor ở bước tiếp theo.

## Chạy local

Yêu cầu .NET 10 SDK.

PowerShell:

```powershell
$env:ADMIN_PASSWORD = "change-this-password"
$env:MASTER_ENCRYPTION_KEY = "replace-with-at-least-24-random-characters"
$env:PORT = "8080"
dotnet run -- master
```

Mở `http://localhost:8080`, đăng nhập Basic Auth bằng `admin` và mật khẩu trên. Tạo vệ tinh trong dashboard, sao chép token rồi chạy terminal thứ hai:

```powershell
$env:PORT = "8081"
dotnet run -- satellite --master-url http://localhost:8080 --agent-token "TOKEN_VUA_TAO" --name "local-01" --slots 1
```

## Deploy hai Render Web Service

Có thể dùng `render.yaml`, hoặc tạo thủ công hai Web Service cùng trỏ tới repo/Dockerfile này.

### Master

Start command:

```text
dotnet GarenaOrchestrator.dll master
```

Environment bắt buộc:

| Biến | Ý nghĩa |
|---|---|
| `ADMIN_USER` | Tài khoản admin, mặc định `admin` |
| `ADMIN_PASSWORD` | Mật khẩu dashboard |
| `MASTER_ENCRYPTION_KEY` | Chuỗi ngẫu nhiên tối thiểu 24 ký tự; không thay sau khi đã lưu proxy |
| `PUBLIC_URL` | URL master, ví dụ `https://garena-master.onrender.com` |
| `TURSO_URL` | URL database Turso |
| `TURSO_TOKEN` | Token Turso |

Nếu không đặt Turso, master lưu ở `data/orchestrator-state.json`. Filesystem Render không bền vững, vì vậy production trên Render nên đặt Turso.

Master tự tạo bảng `orchestrator_state`. Không cần chạy migration thủ công.

### Satellite

Start command:

```text
dotnet GarenaOrchestrator.dll satellite
```

Environment:

| Biến | Ý nghĩa |
|---|---|
| `MASTER_URL` | Public URL của master |
| `AGENT_TOKEN` | Token tạo từ dashboard master |
| `SATELLITE_NAME` | Tên hiển thị |
| `SATELLITE_SLOTS` | Số slot, hiện dùng để báo capacity |

Satellite mở `/health` trên `$PORT`, vì vậy có thể chạy dưới dạng Render Web Service. Kết nối về master luôn đi trực tiếp; proxy chỉ được dùng cho phép thử `/api/ip`. IP trực tiếp master quan sát được chính là IP cần nhập vào allowlist của nhà cung cấp proxy.

> **Quan trọng:** Render không đảm bảo một IP outbound duy nhất cho service mặc định. Một service có thể dùng bất kỳ IP nào trong các CIDR của region. Dashboard hiển thị IP đang được quan sát để chẩn đoán; để allowlist ổn định, sao chép toàn bộ dải tại **Render service → Connect → Outbound**, hoặc dùng Dedicated Outbound IP. Xem [Render Outbound IP Addresses](https://render.com/docs/outbound-ip-addresses).

## Cách kiểm tra proxy

1. Tạo satellite và chờ trạng thái Online.
2. Sao chép cột **IP VPS để whitelist**.
3. Thêm IP đó vào allowlist phía nhà cung cấp proxy.
4. Thêm proxy trong dashboard và gán cho satellite.
5. Satellite kiểm tra mỗi 60 giây và báo `Proxy IP`, độ trễ hoặc lỗi.

Master không bao giờ trả proxy password qua admin API. Password được mã hóa AES-GCM bằng `MASTER_ENCRYPTION_KEY`, chỉ được giải mã khi gửi qua HTTPS tới satellite đã xác thực.

## Build Docker

```text
docker build -t garena-orchestrator .
docker run --rm -p 8080:8080 -e PORT=8080 -e ADMIN_PASSWORD=change-me -e MASTER_ENCRYPTION_KEY=replace-with-a-long-random-secret garena-orchestrator master
```
