# v-panel

**Tiếng Việt** | [English](README_EN.md)

v-panel là admin panel tự host để quản lý ứng dụng trên một VPS. Anh cài v-panel lên server, đăng nhập qua web và cài thêm các web app khác bằng **docker compose**.

- Khai báo **app** gồm tên, mô tả, loại app, nội dung `docker-compose.yml` và `.env`, soạn trong editor kiểu IDE (Monaco).
- **Lưu nháp** chỉ ghi metadata. **Deploy** để engine chạy `docker compose` thật, log hiện trực tiếp trên màn hình.
- Một app có thể chạy thành nhiều **instance** (`procurement-01`, `procurement-02`...). Mỗi instance có URL, thư mục cài, `.env` override và dữ liệu (volume) riêng.
- Container dùng chung (**cloudflared** + **Caddy** + **PostgreSQL 18**) cũng là một app và được quản lý ngay trên v-panel. Web public đi theo đường Cloudflare → Cloudflare Tunnel → Caddy → app, nên server không phải mở cổng 80/443. Khi deploy, Caddy được cấu hình tự động theo *Public URL* của instance.
- Có lịch sử deploy kèm snapshot cấu hình, danh sách container, log container, start/stop/restart/remove và audit log.
- Có **MCP server** (`/mcp`) để AI agent như Claude Code, Codex điều khiển panel bằng API token, ví dụ tự cài một app từ GitHub repo. Xem [docs/mcp.md](docs/mcp.md).
- Cài được lên **VPS đã có sẵn stack**: scan rồi nhận quản lý tại chỗ shared-infra (Caddy, Postgres...) và các app compose cài tay, không di chuyển dữ liệu. Xem [docs/migrate-existing-vps.md](docs/migrate-existing-vps.md).

Stack: .NET 10, Blazor Server, MudBlazor, EF Core + SQLite, YamlDotNet, BlazorMonaco, MCP C# SDK.

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

Lần đầu truy cập qua SSH tunnel (xem [Truy cập v-panel](#truy-cập-v-panel)):

```bash
ssh -N -L 5080:127.0.0.1:5080 user@server
```

Mở http://localhost:5080 rồi tạo tài khoản admin. Cũng có thể tạo từ shell, xem [Người dùng và tài khoản admin](#người-dùng-và-tài-khoản-admin).

Sau đó:

1. Tạo Cloudflare Tunnel và điền token vào `CLOUDFLARE_TUNNEL_TOKEN` ở tab `.env` của app *Shared infrastructure*, xem [Cloudflare Tunnel](#cloudflare-tunnel). Mật khẩu Postgres cũng nằm ở đây, được sinh ngẫu nhiên.
2. Deploy instance **shared-infra** để chạy cloudflared, Caddy và PostgreSQL 18. Không container nào publish cổng web ra host.
3. Tạo app, thêm instance, điền Public URL, khai báo hostname tương ứng trên Cloudflare, rồi bấm **Deploy**.
4. Tuỳ chọn: vào **Settings → v-panel public URL**, điền ví dụ `https://panel.example.com` để vào panel qua tunnel. Caddy gọi tới v-panel qua network chung bằng địa chỉ `vpanel:8080`.

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
> - Container được mount `/var/run/docker.sock` và chạy bằng root. Quyền điều khiển Docker tương đương root trên server, nên cổng 5080 chỉ mở trên `127.0.0.1`. Nếu public panel qua Cloudflare Tunnel thì dùng mật khẩu mạnh và nên bật Cloudflare Access cho hostname đó.
> - `/opt/vpanel` được mount **cùng đường dẫn** bên trong và bên ngoài container. Docker trên host đọc các đường dẫn bind mount trong compose của app theo đường dẫn host, nên không được đổi vế trái hay vế phải của mount này.

### Image trên GHCR

Workflow [`.github/workflows/ci.yml`](.github/workflows/ci.yml) chạy test cho mọi PR. Khi push lên `main` hoặc tag `v*`, workflow build image `linux/amd64` + `linux/arm64` và đẩy lên `ghcr.io/lockerbill/v-panel`:

- push lên `main` tạo tag `latest`;
- tag `v1.2.3` tạo tag `1.2.3` và `1.2`.

Lần đầu, package GHCR ở chế độ private. Vào *Package settings* để chuyển sang public, hoặc chạy `docker login ghcr.io` trên server.

## Truy cập v-panel

Có hai cách, dùng song song được.

**SSH tunnel** luôn dùng được, kể cả khi chưa deploy shared-infra hoặc khi Caddy/cloudflared đang lỗi. v-panel chỉ lắng nghe trên `127.0.0.1:5080` của server:

```bash
ssh -N -L 5080:127.0.0.1:5080 user@server
```

Rồi mở http://localhost:5080. Nếu cổng 5080 trên máy anh đã bận thì đổi cổng bên trái, ví dụ `-L 15080:127.0.0.1:5080` rồi mở http://localhost:15080. Muốn khỏi gõ lại thì thêm vào `~/.ssh/config`:

```
Host vpanel
    HostName server.example.com
    User user
    LocalForward 5080 127.0.0.1:5080
```

Sau đó chỉ cần `ssh -N vpanel`.

**Public URL qua Cloudflare Tunnel:** điền **Settings → v-panel public URL** (ví dụ `https://panel.example.com`) và thêm hostname đó trên Cloudflare như mọi app khác. Panel có quyền tương đương root, nên nên đặt hostname này sau một Cloudflare Access application (Zero Trust → Access → Applications) để đăng nhập Cloudflare trước khi tới form login của v-panel. Blazor dùng WebSocket, Cloudflare Tunnel hỗ trợ sẵn.

Đăng nhập qua hai đường không ảnh hưởng nhau: qua SSH tunnel là `http://localhost` (cookie không Secure), qua tunnel là `https://panel…` (cookie Secure), mỗi origin giữ phiên riêng.

## Cloudflare Tunnel

Luồng request: trình duyệt → Cloudflare (HTTPS) → `cloudflared` (container trong shared-infra, kết nối ra ngoài tới Cloudflare) → `http://caddy:80` qua network `vpanel-shared` → app. Caddy không publish cổng nào nên server không cần mở 80/443, và không ai đi vòng qua Cloudflare để vào thẳng app được.

1. Cloudflare dashboard → **Zero Trust → Networks → Tunnels → Create a tunnel**, chọn *Cloudflared*. Copy token trong lệnh cài (chuỗi sau `--token`) vào `CLOUDFLARE_TUNNEL_TOKEN` trong `.env` của app *Shared infrastructure*, rồi deploy **shared-infra**.
2. Ở tab **Public Hostname** của tunnel, thêm từng hostname (ví dụ `p1.example.com`), *Service* là `HTTP` và URL `caddy:80`. Có thể thêm một wildcard `*.example.com` trỏ tới `caddy:80` để khỏi khai báo từng app; khi đó tự tạo bản ghi DNS `*` (CNAME tới `<tunnel-id>.cfargotunnel.com`) nếu dashboard chưa tạo.
3. **Settings → Public ingress** phải là *Cloudflare Tunnel* (mặc định với bản cài mới).

Ở chế độ này v-panel sinh site Caddy dạng `http://p1.example.com { … }`: TLS kết thúc ở Cloudflare, Caddy không xin cert. Với Public URL `https://…`, Caddy gửi `X-Forwarded-Proto: https` tới app để app không redirect lặp sang https. IP thật của người dùng nằm ở header `CF-Connecting-IP`.

**Chế độ Direct (không dùng Cloudflare):** đặt **Settings → Public ingress = Direct**, sửa compose của *Shared infrastructure*: bỏ comment khối `ports` của Caddy (80, 443) và xoá service `cloudflared`, rồi deploy. Caddy sẽ tự xin cert HTTPS như trước.

**Chuyển bản cài cũ (Caddy đang mở 80/443) sang tunnel.** Template đã lưu trong v-panel không tự đổi, nên làm tay:

1. **Settings → Public ingress = Cloudflare Tunnel** rồi *Save*. v-panel sinh lại các site Caddy thành `http://…`.
2. Sửa compose của *Shared infrastructure* theo [`samples/infra/docker-compose.yml`](samples/infra/docker-compose.yml): bỏ `ports` của Caddy, thêm service `cloudflared`.
3. Thêm `CLOUDFLARE_TUNNEL_TOKEN=…` vào `.env` của app, khai báo hostname trên Cloudflare như trên.
4. Deploy **shared-infra**.

Muốn quay lại Direct thì làm ngược lại.

## Người dùng và tài khoản admin

- **Lần đầu mở panel:** trang `/setup` cho tạo tài khoản admin đầu tiên. Trang này tự khoá khi đã có user.
- **Trang Users** (menu bên trái): xem danh sách user, tạo user mới, đặt lại mật khẩu và xoá user. Không xoá được chính mình hay user cuối cùng. User bị xoá sẽ bị đăng xuất ở request kế tiếp. Mọi user đều là admin.
- **Đổi mật khẩu của chính mình:** *Settings → Change password*.

Tạo admin hoặc đặt lại mật khẩu từ shell trên server, ví dụ khi muốn tạo admin trước khi mở panel hoặc khi quên mật khẩu:

```bash
docker exec -it vpanel vpanel create-admin
```

```bash
docker exec -it vpanel vpanel reset-password --username admin
```

Lệnh sẽ hỏi username (mặc định `admin`) và mật khẩu (ẩn khi gõ). Dùng trong script thì truyền mật khẩu qua stdin:

```bash
echo 'mat-khau-manh' | docker exec -i vpanel vpanel create-admin --username alice --password-stdin
```

Chạy không có terminal (`docker exec` thiếu `-it`) thì `create-admin` tự sinh mật khẩu ngẫu nhiên và in ra một lần. Bản cài bằng systemd dùng `sudo -u vpanel /opt/vpanel/app/VPanel.Web create-admin`. Xem toàn bộ tuỳ chọn bằng `vpanel help`.

## Điều khiển bằng AI agent (MCP)

v-panel có MCP server ở `/mcp` để Claude Code, Codex... tạo app, tạo instance, deploy và đọc log thay anh. Ví dụ đưa agent một GitHub repo, agent tự viết compose theo quy ước của v-panel, cài lên server và sửa đến khi app chạy.

1. Tạo token ở trang **API tokens**, hoặc từ shell:

   ```bash
   docker exec vpanel vpanel create-token --name claude-laptop
   ```

2. Mở SSH tunnel tới server (`ssh -N -L 5080:127.0.0.1:5080 user@server`), rồi khai báo với agent, ví dụ Claude Code:

   ```bash
   claude mcp add --transport http vpanel http://localhost:5080/mcp --header "Authorization: Bearer vp_xxx"
   ```

3. Yêu cầu agent, ví dụ: *"Cài https://github.com/owner/repo lên server qua v-panel, public ở https://app.example.com"*.

Token có toàn quyền admin. Mọi thao tác qua MCP ghi audit log dưới tên `mcp:<token>`. Cấu hình Codex, danh sách tool và quy trình cài app xem ở [docs/mcp.md](docs/mcp.md).

## PostgreSQL dùng chung (bản 18)

Instance **shared-infra** chạy image `postgres:18`. Từ bản 18, image chính thức lưu dữ liệu ở `/var/lib/postgresql/18/docker`, nên volume được mount vào `/var/lib/postgresql` thay vì `/var/lib/postgresql/data` như trước.

**Nâng cấp bản cài cũ đang chạy PostgreSQL 17.** Template đã lưu trong v-panel không tự đổi, vì dữ liệu của bản 17 không đọc được trực tiếp bằng bản 18. Muốn lên 18 thì làm như sau:

1. Dump toàn bộ dữ liệu:

   ```bash
   docker exec postgres pg_dumpall -U postgres > pg17-dump.sql
   ```

2. Trong v-panel, mở app *Shared infrastructure* và sửa compose: `image: postgres:18`, volume `postgres_data:/var/lib/postgresql`.
3. Dừng instance rồi xoá volume cũ (đã dump ở bước 1):

   ```bash
   docker compose -p vp-shared-infra down
   ```

   ```bash
   docker volume rm vp-shared-infra_postgres_data
   ```

4. Bấm **Deploy** instance *shared-infra*, đợi Postgres healthy rồi nạp lại dữ liệu:

   ```bash
   docker exec -i postgres psql -U postgres < pg17-dump.sql
   ```

## Cài lên VPS đã có sẵn stack (import)

Nếu VPS đã có shared-infra (Caddy, Postgres...) và các app docker compose cài tay từ trước, v-panel có thể nhận quản lý chúng **tại chỗ**: không di chuyển thư mục, không đổi tên compose project, không tạo volume mới.

Dưới đây là tóm tắt. Hướng dẫn đầy đủ (chuẩn bị, backup, kiểm tra từng bước, ví dụ cụ thể, rollback, xử lý sự cố) ở [docs/migrate-existing-vps.md](docs/migrate-existing-vps.md).

1. Cài v-panel ở chế độ adopt:

   ```bash
   sudo ./deploy/docker-install.sh --adopt
   ```

   Script tìm container Caddy đang chạy và dùng network của nó làm shared network (ví dụ `proxy`). Từ thư mục làm việc của các compose project có sẵn, nó suy ra các thư mục gốc (`/opt/infra`, `/opt/n8n-arm`, `/opt/<repo>`...), mount chúng vào container vpanel ở **cùng đường dẫn** và khai báo chúng là `VPanel:AdoptedRoots`. Muốn tự chọn thư mục thì truyền vào sau cờ: `--adopt /opt/infra /opt/apps`. Lựa chọn được lưu ở `/opt/vpanel/vpanel.env` và `/opt/vpanel/docker-compose.adopt.yml`, các lần nâng cấp sau dùng lại.
2. Vào trang **Import**, bấm **Scan server**. Scan chỉ đọc, không đụng vào gì. Kết quả gồm:
   - stack shared-infra, kèm các setting sẽ áp dụng: network, tên container Caddy, thư mục Caddy, chế độ ingress;
   - danh sách app;
   - các site Caddy viết tay, kèm thông tin site đó trỏ tới app nào.
3. Import **shared-infra trước**. v-panel cập nhật Settings theo stack này, bỏ template `shared-infra` mặc định (nếu chưa deploy lần nào) và thêm `import …/sites/vp-*.caddy` vào Caddyfile nếu còn thiếu (có backup và chạy validate).
4. Import từng app. Proxy service/port, Public URL và `tls internal` được gợi ý từ site Caddy viết tay. Container vẫn chạy như cũ; compose và `.env` gốc được copy vào `<thư mục app>/.vpanel-backup/`.
5. Trên mỗi instance vừa import, bấm **Preview deploy**: nó cho biết service nào sẽ bị recreate. Sau đó bấm **Deploy** một lần để v-panel quản lý hẳn. Lần deploy này ghi file của v-panel. Nếu Public URL đang do một site Caddy viết tay phục vụ, site đó được thay bằng file `vp-*.caddy` ngay trong cùng một lần reload. Các directive khác (`import common`, `header {…}`, `log`…) và option của `reverse_proxy` (health check…) được giữ nguyên văn. File cũ được chuyển vào `<thư mục Caddy>/.vpanel-backup/`.

Lưu ý:

- Service đã có alias trên shared network (`aliases: [n8n-arm]`) giữ nguyên alias đó làm upstream. v-panel không sinh `docker-compose.vpanel.yml`, nên container không bị recreate.
- Stack vừa có `image:` vừa có `build:` nên dùng chế độ **Pull first** (gợi ý sẵn khi import). Chế độ này pull image đã publish và chỉ build những image còn thiếu, giống `docker compose pull && docker compose up -d`.
- Take over được khi site có một địa chỉ và đúng một `reverse_proxy host:port`. Site có định tuyến (`handle`, `handle_path`, `route`, `redir`, matcher `@…`, nhiều upstream) được giữ nguyên là file viết tay; instance đó để trống Public URL.
- Directive và option giữ lại có thể sửa trong cấu hình instance (**Caddy site directives**, **reverse_proxy options**). Snippet như `(common)` vẫn nằm trong Caddyfile chính và dùng được trong file `vp-*.caddy`.
- Instance import mà chưa deploy lần nào thì có thể "Stop managing": v-panel quên nó đi, container và file không bị đụng tới.
- Chế độ systemd: khai báo `VPanel:AdoptedRoots` trong `/etc/vpanel/appsettings.json` và cấp quyền đọc/ghi các thư mục đó cho user `vpanel`.

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

v-panel lắng nghe ở `127.0.0.1:5080` (dùng cho SSH tunnel) và ở IP của bridge `docker0`, để Caddy proxy vào qua `host.docker.internal:5080`. Cấu hình riêng của server nằm ở `/etc/vpanel/appsettings.json` và `/etc/vpanel/vpanel.env`. Xem log bằng `journalctl -u vpanel -f`.

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
dotnet run --project src/VPanel.Web -- create-admin   # tạo admin trong dữ liệu dev
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
