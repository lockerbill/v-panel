# v-panel

**Tiếng Việt** | [English](README_EN.md)

v-panel là admin panel tự host để quản lý ứng dụng trên một VPS. Anh cài v-panel lên server, đăng nhập qua web và cài thêm các web app khác bằng **docker compose**.

- Khai báo **app** gồm tên, mô tả, loại app, nội dung `docker-compose.yml` và `.env`, soạn trong editor kiểu IDE (Monaco).
- **Lưu nháp** chỉ ghi metadata. **Deploy** để engine chạy `docker compose` thật, log hiện trực tiếp trên màn hình.
- Một app có thể chạy thành nhiều **instance** (`procurement-01`, `procurement-02`...). Mỗi instance có URL, thư mục cài, `.env` override và dữ liệu (volume) riêng.
- Container dùng chung (**Caddy** + **PostgreSQL**) cũng là một app và được quản lý ngay trên v-panel. Khi deploy, Caddy được cấu hình tự động theo *Public URL* của instance (có HTTPS tự động).
- Có lịch sử deploy kèm snapshot cấu hình, danh sách container, log container, start/stop/restart/remove và audit log.

Stack: .NET 10, Blazor Server, MudBlazor, EF Core + SQLite, YamlDotNet, BlazorMonaco.

## Cài bằng Docker (khuyến nghị)

Yêu cầu: Linux (amd64 hoặc arm64) đã có Docker Engine và plugin `docker compose`.

```bash
git clone https://github.com/lockerbill/v-panel.git
```

```bash
cd v-panel && sudo ./deploy/docker-install.sh
```

Script sẽ:

1. Tạo `/opt/vpanel/{apps,caddy,data}`.
2. Tạo network Docker `vpanel-shared`.
3. Build image và chạy container `vpanel` từ [`deploy/docker-compose.yml`](deploy/docker-compose.yml).

Khi image đã có sẵn trên GHCR, có thể bỏ bước build bằng `sudo ./deploy/docker-install.sh --pull`.

<details>
<summary>Cài thủ công, không dùng script</summary>

```bash
sudo mkdir -p /opt/vpanel/apps /opt/vpanel/caddy/sites /opt/vpanel/data
```

```bash
sudo docker network create vpanel-shared
```

```bash
sudo docker compose -f deploy/docker-compose.yml up -d --build
```

</details>

Lần đầu truy cập qua SSH tunnel:

```bash
ssh -L 5080:127.0.0.1:5080 user@server
```

Mở http://localhost:5080 rồi tạo tài khoản admin.

Sau đó:

1. Deploy instance **shared-infra** để chạy Caddy (cổng 80/443) và PostgreSQL. Mật khẩu Postgres được sinh ngẫu nhiên, xem và sửa ở tab `.env` của app *Shared infrastructure*.
2. Vào **Settings → v-panel public URL**, điền ví dụ `https://panel.example.com` để truy cập panel qua HTTPS. Caddy gọi tới v-panel qua network chung bằng địa chỉ `vpanel:8080`.
3. Tạo app, thêm instance, điền Public URL, rồi bấm **Deploy**.

**Nâng cấp:**

```bash
git pull && sudo docker compose -f deploy/docker-compose.yml up -d --build
```

Khi dùng image GHCR thì thay bằng `pull` rồi `up -d`. Các app đang chạy không bị ảnh hưởng khi v-panel khởi động lại.

**Backup:** toàn bộ dữ liệu nằm trong `/opt/vpanel`:

| Thư mục | Nội dung |
|---|---|
| `data/` | SQLite và key mã hoá |
| `apps/` | File của từng instance |
| `caddy/` | Cấu hình Caddy |

Volume dữ liệu của các app (Postgres...) là Docker volume, cần backup riêng.

**Xem log:** `docker logs -f vpanel`

> **Lưu ý bảo mật và cách hoạt động**
> - Container được mount `/var/run/docker.sock` và chạy bằng root. Quyền điều khiển Docker tương đương root trên server, nên cổng 5080 chỉ mở trên `127.0.0.1` và panel chỉ nên public qua Caddy (HTTPS) với mật khẩu mạnh.
> - `/opt/vpanel` được mount **cùng đường dẫn** bên trong và bên ngoài container. Docker trên host đọc các đường dẫn bind mount trong compose của app theo đường dẫn host, nên không được đổi vế trái hay vế phải của mount này.

### Image trên GHCR

Workflow [`.github/workflows/ci.yml`](.github/workflows/ci.yml) chạy test cho mọi PR. Khi push lên `main` hoặc tag `v*`, workflow build image `linux/amd64` + `linux/arm64` và đẩy lên `ghcr.io/lockerbill/v-panel`:

- push lên `main` tạo tag `latest`;
- tag `v1.2.3` tạo tag `1.2.3` và `1.2`.

Lần đầu, package GHCR ở chế độ private. Vào *Package settings* để chuyển sang public, hoặc chạy `docker login ghcr.io` trên server.

## Cách khác: chạy bằng systemd

Dùng cách này nếu muốn v-panel chạy trực tiếp trên host, độc lập với Docker.

```bash
./scripts/publish.sh            # tạo ./publish (self-contained, không cần cài .NET trên server)
```

```bash
sudo ./deploy/install.sh ./publish
```

Script cài đặt sẽ:

- tạo user `vpanel` (thuộc group `docker`);
- tạo các thư mục `/opt/vpanel/app` (binary), `/opt/vpanel/apps`, `/opt/vpanel/caddy` và `/var/lib/vpanel` (dữ liệu);
- cài service systemd `vpanel`.

v-panel lắng nghe ở `127.0.0.1:5080` và ở IP của bridge `docker0`, để Caddy proxy vào qua `host.docker.internal:5080`. Cấu hình riêng của server nằm ở `/etc/vpanel/appsettings.json` và `/etc/vpanel/vpanel.env`. Xem log bằng `journalctl -u vpanel -f`.

## Viết docker compose cho nhiều instance

Mỗi instance là một compose project riêng, tên là `vp-<instance>`. v-panel ghi 3 file vào thư mục cài:

| File | Nội dung |
|---|---|
| `docker-compose.yml` | Template của app, hoặc compose override riêng của instance |
| `.env` | Biến hệ thống của v-panel, `.env` của app, rồi đến override của instance |
| `docker-compose.vpanel.yml` | Sinh tự động: gắn *proxy service* vào network dùng chung với alias duy nhất để Caddy gọi tới |

Biến v-panel tự thêm vào `.env`, dùng được trong compose dưới dạng `${...}`:

| Biến | Ví dụ |
|---|---|
| `VPANEL_INSTANCE` | `procurement-01` |
| `VPANEL_PUBLIC_URL`, `VPANEL_HOST` | `https://p1.example.com`, `p1.example.com` |
| `VPANEL_SHARED_NETWORK` | `vpanel-shared` |
| `VPANEL_PROXY_ALIAS` | `vp-procurement-01-web` |
| `COMPOSE_PROJECT_NAME` | `vp-procurement-01` |

Các biến này đứng đầu file `.env`, nên giá trị khác có thể tham chiếu tới chúng, ví dụ `DB_NAME=procurement_${VPANEL_INSTANCE}`.

**Volume:**

- *Named volume* do docker compose tự tạo, có tiền tố tên project nên mỗi instance có dữ liệu riêng.
- Volume/network khai báo `external: true` được v-panel tạo trước nếu chưa có.
- Thư mục bind tương đối (`./data`) được tạo bên trong thư mục cài.
- Ở tab *Volumes* có thể khai báo thêm external volume dùng chung giữa các app.

**Dùng container chung:** khai báo network ngoài rồi gọi Postgres bằng hostname `postgres`.

```yaml
services:
  web:
    image: my/app
    environment:
      DATABASE_URL: postgres://${DB_USER}:${DB_PASSWORD}@postgres:5432/${DB_NAME}
    networks: [default, shared]
networks:
  shared:
    external: true
    name: ${VPANEL_SHARED_NETWORK}
```

Ví dụ đầy đủ, gồm cả bước tự tạo database riêng cho từng instance, nằm ở [`samples/apps/postgres-app`](samples/apps/postgres-app).

**Public URL:** Caddy route `host[/path]` của instance tới `<proxy service>:<proxy port>`. Proxy service và port khai báo ở app (mặc định) hoặc ghi đè theo instance. Có thể dùng cả domain riêng lẫn đường dẫn con (`https://example.com/p1`).

## Phát triển

```bash
dotnet tool restore
```

```bash
dotnet run --project src/VPanel.Web          # http://localhost:5080, dữ liệu trong src/VPanel.Web/.data
```

```bash
dotnet test
```

```bash
docker build -t v-panel:dev .                # build image
```

```bash
dotnet ef migrations add <Name> -p src/VPanel.Infrastructure -s src/VPanel.Infrastructure -o Data/Migrations
```

Kiến trúc và hướng mở rộng xem ở [docs/architecture.md](docs/architecture.md).
