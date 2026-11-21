# MCP server: cho Claude, Codex điều khiển v-panel

v-panel có sẵn một MCP server ở endpoint `/mcp` (Streamable HTTP). Agent như Claude Code hay Codex kết nối vào đó để tạo app, tạo instance, deploy, xem log... thay cho thao tác trên UI. Use case chính: đưa agent một GitHub repo, agent tự viết `docker-compose.yml` theo quy ước của v-panel, cài lên server và sửa đến khi app chạy.

```
Claude Code / Codex ──HTTP + Bearer token──> v-panel /mcp ──> App / Instance / Deployment services ──> docker compose
        │
        └──SSH (tuỳ chọn)──> server: clone repo private, docker build, kiểm tra bằng curl...
```

## 1. Tạo API token

Mỗi agent nên có một token riêng để dễ thu hồi. Tên token hiện trong audit log dạng `mcp:<tên>`.

- **Trên UI:** menu **API tokens → New token**. Token chỉ hiện một lần, kèm sẵn lệnh cấu hình cho Claude Code và Codex.
- **Từ shell trên server:**

  ```bash
  docker exec vpanel vpanel create-token --name claude-laptop
  ```

  Chỉ token được in ra stdout, nên có thể gán thẳng vào biến: `TOKEN=$(docker exec vpanel vpanel create-token -n ci)`. Thêm `--expires-days 30` nếu muốn token tự hết hạn. Xem và thu hồi bằng `vpanel list-tokens` và `vpanel revoke-token --name claude-laptop`.

> **Token có toàn quyền admin**, tức là có quyền điều khiển Docker, tương đương root trên server. Chỉ để token ở máy của anh và thu hồi ngay khi bị lộ. Token không đăng nhập được vào UI, và cookie đăng nhập UI cũng không gọi được `/mcp`.

## 2. Cho agent kết nối tới `/mcp`

v-panel chỉ lắng nghe trên `127.0.0.1:5080` của server, nên có hai cách:

**SSH tunnel** (khuyến nghị, không phải public gì thêm). Giữ lệnh này chạy trong lúc agent làm việc:

```bash
ssh -N -L 5080:127.0.0.1:5080 user@server
```

Endpoint lúc đó là `http://localhost:5080/mcp`.

**Public URL qua Cloudflare Tunnel:** nếu đã đặt *Settings → v-panel public URL* (ví dụ `https://panel.example.com`) thì endpoint là `https://panel.example.com/mcp`. Nếu hostname này nằm sau Cloudflare Access, request của agent sẽ bị Access chặn trước khi tới v-panel. Khi đó có hai lựa chọn: dùng SSH tunnel, hoặc tạo một Access *service token*, thêm policy *Service Auth* cho nó, rồi gửi kèm hai header `CF-Access-Client-Id` và `CF-Access-Client-Secret` trong cấu hình client (Claude Code: thêm `--header`, Codex: `http_headers`).

### Claude Code

```bash
claude mcp add --transport http vpanel http://localhost:5080/mcp --header "Authorization: Bearer vp_xxx"
```

Thêm `--scope user` nếu muốn dùng ở mọi project. Kiểm tra bằng `claude mcp list` hoặc `/mcp` trong Claude Code.

### Codex

Thêm vào `~/.codex/config.toml`:

```toml
[mcp_servers.vpanel]
url = "http://localhost:5080/mcp"
bearer_token_env_var = "VPANEL_TOKEN"
```

Rồi đặt token trong môi trường trước khi chạy `codex`:

```bash
export VPANEL_TOKEN=vp_xxx
```

### Client khác

Mọi client hỗ trợ MCP Streamable HTTP đều dùng được: URL như trên, header `Authorization: Bearer <token>`. Server chạy ở chế độ stateless, không cần giữ session.

## 3. Cài app từ một GitHub repo

Cho agent quyền SSH tới server (ví dụ Claude Code chạy trên máy anh, có sẵn `ssh user@server`) và MCP `vpanel`, rồi yêu cầu:

> Cài app https://github.com/owner/repo lên server qua v-panel, public ở https://notes.example.com. Dùng Postgres dùng chung.

Client nào hỗ trợ MCP prompt thì có thể chọn prompt `install_app_from_repo` (trong Claude Code là `/mcp__vpanel__install_app_from_repo`).

Server gửi sẵn hướng dẫn ngắn cho agent khi kết nối, và tool `get_install_guide` trả về quy trình đầy đủ ([nội dung](../src/VPanel.Web/Mcp/McpGuide.cs)). Agent sẽ:

1. Đọc repo: cách đóng gói (image có sẵn, `Dockerfile`, compose có sẵn), cổng HTTP, biến môi trường, database, dữ liệu cần lưu.
2. Chọn cách có image:
   - Image đã publish thì dùng `image:`.
   - Repo public có `Dockerfile` thì build thẳng từ git, server không cần file nào: `build: { context: https://github.com/owner/repo.git#main }`.
   - Repo private hoặc cần sửa thì qua SSH `git clone` vào `<thư mục cài>/src` rồi dùng `build: ./src`, hoặc `docker build` trên server.
3. Viết compose theo quy ước v-panel: không publish `ports`, không dùng `container_name`, khai báo proxy service/port, dùng named volume, biến `${VPANEL_*}`, network `${VPANEL_SHARED_NETWORK}` nếu dùng Postgres chung.
4. `validate_compose` → `save_app` → `save_instance` → `run_instance_action(deploy)`, đọc log deploy và log container, sửa rồi deploy lại cho đến khi chạy.
5. Báo lại URL, secret đã sinh, và việc anh cần làm thêm (thêm hostname trên Cloudflare Tunnel).

Mọi thứ agent tạo đều hiện trên UI như khi anh tự làm. Agent sửa tiếp được, anh cũng sửa tay được.

## 4. Danh sách tool

App và instance được tham chiếu bằng id hoặc slug.

| Tool | Việc |
|---|---|
| `get_panel_info` | Apps root, network dùng chung, chế độ ingress, biến `VPANEL_*`, service dùng chung, instance hệ thống |
| `get_install_guide` | Quy ước viết compose và quy trình cài app |
| `validate_compose` | Kiểm tra compose offline, cảnh báo thêm các lỗi hay gặp khi chạy sau Caddy (publish port, `container_name`, proxy service không tồn tại) |
| `list_apps`, `get_app` | Xem app, gồm compose và `.env` đã giải mã |
| `save_app` | Tạo app, hoặc sửa từng trường của app có sẵn (trường không truyền giữ nguyên) |
| `delete_app` | Xoá app không còn instance (không xoá được app hệ thống `shared-infra`) |
| `list_instances`, `get_instance` | Trạng thái, URL, proxy alias, container đang chạy, các lần deploy gần đây |
| `save_instance` | Tạo hoặc sửa instance: Public URL, `.env` override, compose override, proxy |
| `run_instance_action` | `deploy`, `start`, `stop`, `restart`, `down`, có thể chờ job xong rồi trả log |
| `remove_instance` | Gỡ container và route, tuỳ chọn xoá volume (mất dữ liệu) và thư mục cài |
| `delete_instance` | Xoá bản ghi instance đã remove hoặc chưa deploy |
| `list_deployments`, `get_deployment` | Lịch sử job và log (log trực tiếp khi job đang chạy) |
| `get_container_logs` | Log container của instance (`docker compose logs --tail`) |

Prompt: `install_app_from_repo(repo_url, public_url?, instance_name?)`.

Agent được làm mọi thao tác như admin, kể cả xoá. Hướng dẫn gửi kèm yêu cầu agent hỏi anh trước khi làm thao tác phá huỷ (xoá volume, xoá app/instance, sửa `shared-infra`). Mọi thao tác đều ghi audit log với user `mcp:<tên token>`, và lịch sử deploy ghi *Triggered by* tương ứng.

## 5. Kiểm tra nhanh bằng curl

```bash
curl -s http://localhost:5080/mcp \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -H "MCP-Protocol-Version: 2025-06-18" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

Thiếu hoặc sai token sẽ trả `401` kèm `WWW-Authenticate: Bearer`.
