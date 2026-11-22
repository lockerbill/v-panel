# Kiến trúc v-panel

```
Browser ──HTTPS──> Cloudflare ──tunnel──> cloudflared ──HTTP──> Caddy ──> app (vp-<instance>-<service>:<port>)
                          (cloudflared + Caddy: shared-infra)     │
                                                                  └──> v-panel (vpanel:8080)
SSH tunnel: ssh -L 5080:127.0.0.1:5080 ──────────────────────────────────────> v-panel (127.0.0.1:5080)
                                                     │
   Blazor UI (MudBlazor, Monaco) ───────────────────┤
   MCP /mcp (Bearer API token, cho AI agent) ───────┤
   Services: App / Instance / Deployment / Settings │
   DeploymentQueue + DeploymentWorker (nền)         │
   StatusSyncWorker (đồng bộ trạng thái từ Docker)  │
   IAppTypeHandler ── DockerComposeHandler          │
   IReverseProxyProvider ── CaddyFileProvider       │
   IProcessRunner ── LocalProcessRunner (docker CLI)│
   EF Core ── SQLite (/var/lib/vpanel/vpanel.db)    │
                         │
          docker compose -p vp-<instance> ...
   /opt/vpanel/apps/<instance>/{docker-compose.yml,.env,docker-compose.vpanel.yml}
```

## Chế độ chạy

| | Docker (khuyến nghị) | systemd |
|---|---|---|
| Cách chạy | Container `vpanel` (`Dockerfile`, `deploy/docker-compose.yml`) | Binary self-contained, service `vpanel` (`deploy/install.sh`) |
| Điều khiển Docker | Docker CLI và plugin compose nằm trong image, gọi host qua `/var/run/docker.sock` được mount vào | Docker CLI của host, user `vpanel` thuộc group `docker` |
| Dữ liệu | `/opt/vpanel/data` | `/var/lib/vpanel` |
| Caddy gọi v-panel | `vpanel:8080` qua network `vpanel-shared` | `host.docker.internal:5080`, v-panel bind thêm IP `docker0` |

Ở chế độ container, `/opt/vpanel` phải được mount **cùng đường dẫn** bên trong và bên ngoài. v-panel ghi file compose vào đó, còn Docker daemon trên host đọc các đường dẫn bind mount và `--project-directory` theo đường dẫn host. Container v-panel không phải một instance (tên project không có tiền tố `vp-`), nên v-panel không thể tự stop hay remove chính nó.

Lệnh bảo trì (`create-admin`, `reset-password`, `create-token`, `list-tokens`, `revoke-token`, `help`) dùng chung binary với web: `Program.cs` nhận ra lệnh ở tham số đầu tiên, dựng host với cùng cấu hình và database, chạy migration rồi thực thi lệnh thay vì khởi động web (`VPanel.Web/Cli/AdminCommands.cs`). Trong image, `/usr/local/bin/vpanel` (`deploy/vpanel-cli.sh`) gọi tới binary này.

Image được build đa kiến trúc (amd64/arm64): stage SDK chạy native rồi cross-publish bằng `-a $TARGETARCH`, còn stage runtime không có lệnh `RUN` nào.

## Project

| Project | Vai trò |
|---|---|
| `VPanel.Core` | Entity, enum, interface (điểm mở rộng) và tiện ích thuần: `EnvFile`, `Slug`, `PathGuard`, `UrlHelper`. Không phụ thuộc hạ tầng. |
| `VPanel.Infrastructure` | EF Core/SQLite, engine docker compose, Caddy, hàng đợi deploy, các service nghiệp vụ, seed. |
| `VPanel.Web` | Blazor Server UI, xác thực cookie, cấu hình DI và host. |
| `VPanel.Tests` | xUnit: unit test logic thuần, test pipeline với `FakeProcessRunner`, test service trên SQLite thật. |

## Mô hình dữ liệu

- **AppDefinition** (bảng `Apps`) là template, gồm compose, `.env` (mã hoá), proxy service/port mặc định và `IsSystem` cho hạ tầng dùng chung.
- **AppVolume** là external volume khai báo tay.
- **AppInstance** (bảng `Instances`) là một bản deploy. Nó có slug (tên compose project là `vp-<slug>`), Public URL, thư mục cài, `.env` override (mã hoá), compose override, proxy service/port, trạng thái, `DeployedConfigHash` và `ServerId` (v1 luôn là `local`).
- **Deployment** là một job (Deploy/Start/Stop/Restart/Down/Remove). Mỗi job lưu log, lỗi, snapshot compose và `.env` lúc deploy, và hash cấu hình.
- **Setting** lưu cấu hình runtime dạng JSON. **AuditLog** ghi lại ai làm gì.

Hash cấu hình được tính trên các file render ra cộng các thông số proxy. Hash hiện tại khác `DeployedConfigHash` thì UI báo "changes not deployed". Sửa template của app cũng làm mọi instance của app đó hiện cảnh báo này.

## Luồng deploy (`DockerComposeHandler`)

1. Render (`ComposeRenderer`, hàm thuần): chọn compose, ghép `.env` của app với override của instance và chèn biến hệ thống lên đầu, rồi sinh `docker-compose.vpanel.yml`.
2. Validate offline bằng YamlDotNet, kiểm tra thư mục cài nằm trong `AppsRoot` (`PathGuard` chặn cả `..` lẫn symlink trỏ ra ngoài).
3. Ghi file (`.env` để quyền 600) và đảm bảo network dùng chung đã tồn tại.
4. Chạy `docker compose config --format json` để Docker validate và trả về model đã nội suy. Từ model đó, tạo trước các external network/volume và thư mục bind.
5. `pull --ignore-buildable`, rồi `up -d --remove-orphans` (thêm `--build` nếu có `build:`), rồi `ps` để xác định trạng thái.
6. Worker lưu trạng thái rồi `ProxyRouteService` tính lại toàn bộ bảng route từ DB và gọi `IReverseProxyProvider.ApplyAsync`.

Lệnh docker luôn được gọi qua `ProcessStartInfo.ArgumentList`, không đi qua shell. Job của cùng một instance chạy tuần tự, còn các instance khác nhau chạy song song (giới hạn bởi `MaxConcurrentDeployments`). Khi v-panel khởi động lại, job đang chạy dở bị đánh dấu Failed và job đang chờ được đưa lại vào hàng đợi.

## MCP server

`VPanel.Web/Mcp` đăng ký MCP server (gói `ModelContextProtocol.AspNetCore`, Streamable HTTP, stateless) tại `/mcp`. Endpoint chỉ nhận scheme xác thực `ApiToken` (`Auth/ApiTokenAuthenticationHandler.cs`, header `Authorization: Bearer vp_...`), còn UI vẫn chỉ nhận cookie. Hai đường không dùng lẫn được.

- **ApiToken** (bảng `ApiTokens`) chỉ lưu SHA-256 của secret cùng vài ký tự đầu để nhận diện. `ApiTokenService` lo tạo, thu hồi, kiểm tra hạn và cập nhật `LastUsedAt` (tối đa mỗi phút một lần).
- Các tool (`PanelTools`, `AppTools`, `InstanceTools`) chỉ gọi lại service có sẵn (`AppService`, `InstanceService`, `DeploymentService`...), nên validate, audit và hàng đợi deploy giống hệt UI. User ghi audit là `mcp:<tên token>`. Tool trả DTO gọn thay vì entity EF. Lỗi nghiệp vụ được trả về dạng tool error (`McpException`) để agent đọc và tự sửa.
- `McpGuide` chứa server instructions (gửi khi client kết nối) và hướng dẫn cài app đầy đủ (tool `get_install_guide`, prompt `install_app_from_repo`). Đổi quy ước compose thì sửa cả file này.

## Caddy

Caddy chạy trong container `caddy`, mount thư mục `/opt/vpanel/caddy` vào `/etc/caddy`. File `Caddyfile` chính có dòng `import /etc/caddy/sites/*.caddy`.

`CaddyFileProvider` nhận toàn bộ route mong muốn rồi làm như sau:

1. Ghi mỗi host thành một file `sites/vp-<host>.caddy`. Các route cùng host được gộp lại, phân biệt bằng `handle_path` theo đường dẫn.
2. Xoá các file `vp-*` không còn dùng.
3. Chạy `docker exec caddy caddy validate` rồi `caddy reload`.
4. Nếu validate hoặc reload lỗi thì khôi phục file cũ.

File không có tiền tố `vp-` do người dùng tự viết và không bị đụng tới.

Địa chỉ site phụ thuộc setting **Public ingress** (`PanelSettings.Ingress`):

| | Cloudflare Tunnel (mặc định bản cài mới) | Direct |
|---|---|---|
| Đường vào | Cloudflare → `cloudflared` → `http://caddy:80` qua `vpanel-shared` | Internet → cổng 80/443 Caddy publish ra host |
| Site Caddy | `http://host { … }`, không xin cert | `host { … }`, Caddy tự xin cert |
| `X-Forwarded-Proto` | Route có Public URL `https` được `header_up X-Forwarded-Proto https` | Caddy tự đặt |

Bản cài cũ lưu settings không có field này nên vẫn là `Direct`. `VPanelInitializer` chỉ lưu `CloudflareTunnel` khi seed shared-infra lần đầu (DB trống), khớp với template mới không publish cổng. Đổi chế độ ở trang Settings sẽ sync lại route ngay; file `vp-*` cũ được xoá như mọi lần sync.

Instance được route tới `vp-<instance>-<service>:<port>`. Đây là alias trên network `vpanel-shared`, do file override gắn vào.

## Điểm mở rộng

| Muốn thêm | Cách làm |
|---|---|
| Loại app mới (Dockerfile, static site, systemd...) | Thêm giá trị vào `AppType`, viết class implement `IAppTypeHandler` rồi đăng ký `AddSingleton<IAppTypeHandler, ...>()`. UI tự liệt kê loại mới qua `AppTypeRegistry`. |
| Reverse proxy khác (Nginx, Traefik, Caddy admin API) | Implement `IReverseProxyProvider` và thay bản đăng ký trong `DependencyInjection`. |
| Quản lý nhiều server | Viết `IProcessRunner` chạy qua SSH/agent, rồi chọn runner theo `AppInstance.ServerId`. |
| Lưu secret ở nơi khác (Vault...) | Implement `ISecretProtector`. |
| Rollback | `Deployment` đã lưu snapshot compose/env, chỉ cần thêm action dựng lại file từ snapshot. |
| Nhiều user / phân quyền | `User.Role` và cookie claims đã có sẵn, thêm policy và trang quản lý user. |
| Tool MCP mới | Thêm method `[McpServerTool]` vào một class trong `VPanel.Web/Mcp` (hoặc class mới đăng ký bằng `WithTools<T>()` trong `McpEndpoint`), gọi service có sẵn và trả DTO. |
| Token giới hạn quyền | Thêm cột scope cho `ApiToken`, đưa scope vào claim trong `ApiTokenAuthenticationHandler` và kiểm tra trong tool. |
| Tự cấp database cho instance | Thêm bước provisioner trước `up` (tạo DB/role trong Postgres dùng chung). Hiện làm bằng service `db-init` trong compose, xem `samples/apps/postgres-app`. |
