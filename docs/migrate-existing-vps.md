# Cài v-panel lên VPS đã có sẵn stack và migrate shared-infra, app

[English version](migrate-existing-vps_EN.md)

Hướng dẫn này dành cho server đã chạy docker compose từ trước khi có v-panel: một stack hạ tầng dùng chung (Caddy, có thể kèm PostgreSQL, cloudflared) và các app compose cài tay. v-panel nhận quản lý các stack đó **tại chỗ**. Ví dụ xuyên suốt là server `arm-app-u01`:

| Stack | Thư mục | Ghi chú |
|---|---|---|
| `infra` | `/opt/infra` | Caddy (publish 80/443, local CA) + PostgreSQL 18. Tạo network `proxy` và `db` |
| `n8n-arm` | `/opt/n8n-arm` | Join `proxy` với alias `n8n-arm`, dữ liệu ở `./data` |
| `cxml-dev-server` | `/opt/cxml-dev-server/deploy/selfhost` | Join `proxy` với alias `cxml-app`; có `image:` lẫn `build: ../..` |

Site Caddy viết tay nằm ở `/opt/infra/caddy/sites/*.caddy` và dùng snippet `(common)` (`tls internal`, `encode`, `log`) định nghĩa trong Caddyfile chính.

## Mục lục

1. [v-panel làm gì và không làm gì](#1-v-panel-làm-gì-và-không-làm-gì)
2. [Điều kiện](#2-điều-kiện)
3. [Chuẩn bị và backup](#3-chuẩn-bị-và-backup)
4. [Cài v-panel ở chế độ adopt](#4-cài-v-panel-ở-chế-độ-adopt)
5. [Truy cập v-panel](#5-truy-cập-v-panel)
6. [Scan server](#6-scan-server)
7. [Migrate shared-infra](#7-migrate-shared-infra)
8. [Migrate từng app](#8-migrate-từng-app)
9. [Preview và deploy lần đầu](#9-preview-và-deploy-lần-đầu)
10. [Ví dụ đầy đủ: arm-app-u01](#10-ví-dụ-đầy-đủ-arm-app-u01)
11. [Vận hành sau migration](#11-vận-hành-sau-migration)
12. [Rollback](#12-rollback)
13. [Xử lý sự cố](#13-xử-lý-sự-cố)
14. [Làm bằng AI agent (MCP)](#14-làm-bằng-ai-agent-mcp)
15. [Giới hạn](#15-giới-hạn)

## 1. v-panel làm gì và không làm gì

**Làm:**

- Giữ nguyên **tên compose project** (ví dụ `n8n-arm`, không đổi sang `vp-n8n-arm`). Nhờ vậy container, named volume (`n8n-arm_…`, `infra_caddy_data`…) và network vẫn là những cái cũ.
- Giữ nguyên **thư mục** của stack. Bind mount tương đối như `./data` vẫn trỏ đúng chỗ.
- Giữ nguyên **tên file compose** (`compose.yaml` vẫn là `compose.yaml`).
- **Không restart, không ghi gì** vào stack cho tới khi anh bấm **Deploy** trên instance đó. Lúc import, việc duy nhất v-panel làm trong thư mục stack là copy file gốc vào `.vpanel-backup/` (và tạo `.env` nếu chưa có).
- Service đã có alias trên shared network thì v-panel dùng luôn alias đó làm upstream của route và không sinh `docker-compose.vpanel.yml`, nên container không bị recreate.
- Take over site Caddy viết tay: site đó được thay bằng file `vp-*.caddy` do v-panel sinh ra. Các directive khác (`import common`, `header {…}`…) và option của `reverse_proxy` (health check…) được giữ **nguyên văn**.

**Không làm:**

- Không di chuyển dữ liệu, không đổi tên volume hay network.
- Không động vào site Caddy có định tuyến (`handle`, `handle_path`, `route`, `redir`, matcher `@…`, nhiều upstream). Những site đó tiếp tục là file viết tay.
- Không sửa file Caddy viết tay nào ngoài site được take over.
- Không hỗ trợ reverse proxy khác Caddy (Nginx, Traefik).

## 2. Điều kiện

- Docker Engine và plugin `docker compose`; có quyền root (`sudo`).
- Các stack được chạy bằng `docker compose`, tức container có label `com.docker.compose.*`. Stack đã `down` vẫn được tìm thấy nếu compose file của nó nằm trong một adopted root (xem mục 4).
- Caddy chạy trong container, với **thư mục** cấu hình được mount vào `/etc/caddy` (ví dụ `./caddy:/etc/caddy:ro`). Nếu chỉ mount một file Caddyfile, xem [mục 15](#15-giới-hạn).
- Caddyfile chính import thư mục site (`import sites/*.caddy`). Nếu chưa có, v-panel tự thêm dòng import cho file của nó lúc migrate infra.

## 3. Chuẩn bị và backup

v-panel tự backup những file nó sẽ ghi. Dù vậy, trước một migration nên tự chụp lại hiện trạng:

```bash
docker ps -a --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Label "com.docker.compose.project"}}' > ~/before-ps.txt
```

```bash
docker volume ls > ~/before-volumes.txt && docker network ls > ~/before-networks.txt
```

```bash
sudo tar czf ~/stacks-before-vpanel.tgz /opt/infra /opt/n8n-arm /opt/cxml-dev-server/deploy/selfhost --exclude='*/data/*' --exclude='*/backups/*'
```

- Dữ liệu trong named volume (database, `infra_caddy_data` chứa certificate và local CA) **không bị đụng tới**, vì tên project được giữ nguyên. Backup volume theo quy trình thường lệ của anh nếu cần.
- Nên chạy thử trên một VPS test trước (copy cấu trúc thư mục và compose).

## 4. Cài v-panel ở chế độ adopt

Clone repo về server rồi chạy:

```bash
sudo ./deploy/docker-install.sh --adopt
```

Thêm `--pull` để dùng image `ghcr.io/lockerbill/v-panel:latest` thay vì build từ source. Thêm `--yes` để bỏ bước xác nhận.

Script làm những việc sau:

1. Tìm container Caddy (image `caddy`) và dùng network có nhiều container nhất mà nó join làm **shared network**. Với arm-app-u01 là `proxy`.
2. Lấy thư mục làm việc của mọi compose project (label `com.docker.compose.project.working_dir`) và quy về **thư mục gốc**: `/opt/<x>`, `/srv/<x>`, `/root/<x>`, `/home/<user>/<x>`. Đường dẫn khác thì giữ nguyên. Ví dụ `/opt/cxml-dev-server/deploy/selfhost` → `/opt/cxml-dev-server`, nên build context `../..` cũng nằm trong đó.
   - Thư mục của v-panel (`/opt/vpanel`) và thư mục hệ thống (`/etc`, `/usr`, `/var/lib/docker`…) bị loại.
3. In danh sách và hỏi xác nhận. Muốn tự chọn thư mục thì truyền vào:

   ```bash
   sudo ./deploy/docker-install.sh --adopt /opt/infra /opt/n8n-arm /opt/cxml-dev-server
   ```

4. Ghi 2 file, các lần chạy installer sau (nâng cấp) tự dùng lại:

   `/opt/vpanel/vpanel.env`

   ```ini
   VPANEL_SHARED_NETWORK=proxy
   ```

   `/opt/vpanel/docker-compose.adopt.yml`

   ```yaml
   services:
     vpanel:
       volumes:
         - "/opt/cxml-dev-server:/opt/cxml-dev-server"
         - "/opt/infra:/opt/infra"
         - "/opt/n8n-arm:/opt/n8n-arm"
       environment:
         VPanel__SeedInfrastructure: "false"
         VPanel__Defaults__SharedNetwork: "proxy"
         VPanel__AdoptedRoots__0: "/opt/cxml-dev-server"
         VPanel__AdoptedRoots__1: "/opt/infra"
         VPanel__AdoptedRoots__2: "/opt/n8n-arm"
   ```

   - Mỗi thư mục được mount vào container vpanel **cùng đường dẫn**: v-panel ghi file ở đó, còn Docker daemon trên host đọc theo đường dẫn host.
   - `AdoptedRoots` là danh sách thư mục ngoài `/opt/vpanel/apps` mà instance được phép nằm trong. Danh sách này không sửa được trên UI vì phải khớp với mount.
   - `SeedInfrastructure=false`: không tạo template `shared-infra` mặc định (nó sẽ trùng với Caddy đang chạy).
5. Chạy container `vpanel` trên network `proxy`. Caddy gọi tới v-panel qua `vpanel:8080` nếu anh đặt "v-panel public URL".

Muốn thêm hoặc bớt thư mục thì chạy lại lệnh với `--adopt` và danh sách mới.

**Kiểm tra sau khi cài:**

```bash
docker inspect vpanel --format '{{range .Mounts}}{{.Source}} -> {{.Destination}}{{"\n"}}{{end}}'
```

```bash
docker inspect vpanel --format '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}'
```

```bash
docker logs vpanel --tail 50
```

Trên v-panel, mục **Settings > Server > Adopted roots** phải liệt kê đúng các thư mục trên. Root nào bị từ chối (thư mục hệ thống…) sẽ hiện cảnh báo ở đó.

**Chế độ systemd** (không dùng container): khai báo trong `/etc/vpanel/appsettings.json`:

```json
{
  "VPanel": {
    "AdoptedRoots": [ "/opt/infra", "/opt/n8n-arm", "/opt/cxml-dev-server" ],
    "SeedInfrastructure": false,
    "Defaults": { "SharedNetwork": "proxy" }
  }
}
```

Sau đó cấp quyền đọc/ghi các thư mục đó cho user `vpanel`, rồi `sudo systemctl restart vpanel`. Ở chế độ này Caddy gọi v-panel qua `host.docker.internal:5080`. Caddy của stack infra phải có `extra_hosts: ["host.docker.internal:host-gateway"]` thì route tới chính v-panel mới chạy.

## 5. Truy cập v-panel

Mở SSH tunnel và tạo tài khoản admin như hướng dẫn trong [README](../README.md#truy-cập-v-panel):

```bash
ssh -N -L 5080:127.0.0.1:5080 <user>@<server>
```

Rồi mở `http://localhost:5080`.

## 6. Scan server

Vào **Import** (menu bên trái), bấm **Scan server**. Scan **chỉ đọc**: dùng `docker ps`/`docker inspect`/`docker compose ls`/`docker compose config`, đọc file Caddy, không ghi gì.

Kết quả gồm 3 phần:

- **Shared infrastructure**: stack chạy Caddy. Bảng *Detected / Current* so setting v-panel sẽ áp dụng với setting hiện tại; dòng có "→ will change" là sẽ được đổi khi import. Gồm: thư mục stack, shared network, container Caddy, thư mục Caddy trên host và trong container, ingress (Direct / Cloudflare Tunnel), PostgreSQL (để tham khảo).
- **Hand-written Caddy sites**: mọi site đọc được từ Caddyfile chính và các file nó import (trừ `vp-*`). Cột *Can take over*:
  - `yes`: site có thể thay bằng file v-panel sinh ra;
  - `yes, keeping its directives`: như trên, kèm giữ directive và option;
  - `no (lý do)`: site có định tuyến, giữ nguyên là file viết tay.
- **Apps**: các compose project chưa được quản lý, kèm service, trạng thái và route gợi ý (Public URL → service:port, lấy từ site Caddy trỏ tới alias của app). Nút **Import** chỉ bật khi stack đọc được, nằm trong adopted root, và shared-infra đã được import.

Cảnh báo thường gặp:

| Cảnh báo | Ý nghĩa và cách xử lý |
|---|---|
| `<dir> is not visible to v-panel. Mount it (installer --adopt)…` | Thư mục stack chưa được mount vào container. Chạy lại installer với `--adopt` (kèm thư mục đó). |
| `<dir> is outside the apps root and the adopted roots; add <root> to VPanel:AdoptedRoots.` | Thấy được nhưng chưa được phép: thêm root được gợi ý. |
| `No adopted roots are configured…` | Cài chưa có `--adopt`. |
| `docker compose config: …` | Compose của stack không hợp lệ theo Docker, thường do thiếu biến trong `.env`. Sửa stack trước khi import. |
| `Build context <dir> is not visible…` | Không cản trở import, nhưng muốn build lại thì thư mục build phải được mount. |
| `Started with N compose files; they will be merged…` | Stack chạy với nhiều `-f`. Xem [mục 15](#15-giới-hạn). |
| `Uses env file <file>…` | Stack dùng env file không tên `.env`. Nội dung của nó sẽ thành `.env` do v-panel ghi. |
| `Caddy site '<host>' (…) cannot be taken over (…)` | Site có định tuyến. App vẫn import được, chỉ cần để trống Public URL. |
| `Caddy mounts only <file>…` | Caddy chỉ mount một file. Xem [mục 15](#15-giới-hạn). |
| `Several stacks run Caddy…` | Chỉ stack đầu tiên được coi là infra. |

## 7. Migrate shared-infra

Bấm **Import '<infra>'** trên card *Shared infrastructure*, kiểm tra tên/slug, rồi bấm **Import**.

v-panel làm những việc sau:

1. Copy compose và `.env` gốc vào `/opt/infra/.vpanel-backup/<thời điểm>/`.
2. Xoá template `shared-infra` mặc định nếu có và chưa deploy lần nào.
3. Cập nhật **Settings**: shared network, container Caddy (ví dụ `infra-caddy-1`), thư mục Caddy (`/opt/infra/caddy` ↔ `/etc/caddy`), ingress.
4. Đảm bảo Caddyfile chính load được các file `sites/vp-*.caddy`:
   - Nếu đã có `import sites/*.caddy` (hoặc `sites/*`, `/etc/caddy/sites/…`) thì **không sửa gì**.
   - Nếu chưa có thì backup vào `/opt/infra/caddy/.vpanel-backup/…`, thêm dòng `import /etc/caddy/sites/vp-*.caddy`, chạy `caddy validate` rồi `caddy reload`. Lỗi thì khôi phục file cũ.
5. Tạo app hệ thống và instance **infra** với tên project `infra`, ở trạng thái `imported · not deployed by v-panel`.

**Không có container nào bị restart.**

**Kiểm tra:**

- Vào **Settings**, bấm **Check Caddy**: phải báo container Caddy reachable.
- Vào **Instances > infra**: trạng thái Running, kèm chip "imported".

Có cần deploy infra không? Không bắt buộc. Instance infra có thể ở trạng thái "imported" lâu dài; v-panel vẫn theo dõi trạng thái và cho start/stop/xem log. Khi nào muốn quản lý cả compose của infra thì bấm **Preview deploy** rồi **Deploy** (xem mục 9).

## 8. Migrate từng app

Bấm **Import** ở dòng của app. Các trường trong dialog:

| Trường | Ý nghĩa |
|---|---|
| App name, Slug | Tên trong v-panel. **Tên compose project không đổi** (vẫn là `n8n-arm`). |
| Public URL | Gợi ý từ site Caddy trỏ tới app. Để trống nếu không muốn v-panel quản lý route (site viết tay giữ nguyên). |
| Proxy service, Proxy port | Service và port nhận traffic. Lấy từ upstream của site Caddy (`n8n-arm:5678` → service `n8n`, port 5678). |
| tls internal | Thêm `tls internal` vào site sinh ra. Tắt sẵn khi directive đã lo TLS (ví dụ `import common`); dòng ghi chú dưới công tắc cho biết TLS đang lấy từ đâu. |
| Take over the hand-written Caddy site | Lần deploy đầu thay site viết tay bằng site sinh ra. |
| Caddy site directives | Mọi thứ trong site ngoài `reverse_proxy`, giữ nguyên văn kể cả comment. Được đặt trước `reverse_proxy` trong file sinh ra. |
| reverse_proxy options | Phần trong `reverse_proxy … { }`, ví dụ health check. |
| Images | **Pull first**: pull mọi image, chỉ build image còn thiếu, giống `docker compose pull && docker compose up -d`. Được gợi ý khi service có cả `image:` lẫn `build:`. **Build**: `up --build`, luôn build lại service có `build:`. |

Sau khi import:

- Compose và `.env` gốc được copy vào `<thư mục app>/.vpanel-backup/<thời điểm>/`. Nếu stack chưa có `.env`, v-panel tạo một file `.env` có nội dung env của stack.
- Instance ở trạng thái `imported · not deployed by v-panel`. Container vẫn chạy như cũ; Start/Stop/Restart/Logs dùng chính compose file gốc.
- Lúc này vẫn có thể bỏ: menu **⋮ > Stop managing (keep containers)** xoá instance (và app tạo ra lúc import) khỏi v-panel, không đụng gì tới container, file hay site Caddy.

## 9. Preview và deploy lần đầu

Trên trang instance, bấm **Preview deploy**. v-panel render các file nó sẽ ghi (trong thư mục tạm), chạy `docker compose config --hash '*'`, rồi so với label `com.docker.compose.config-hash` của container đang chạy:

| Action | Ý nghĩa |
|---|---|
| `keep` | Container giữ nguyên |
| `recreate` | Container sẽ được tạo lại (volume và dữ liệu giữ nguyên, có downtime ngắn) |
| `create` | Service chưa có container |
| `remove` | Container không còn service tương ứng (orphan) sẽ bị gỡ |

Nếu có `recreate` ngoài dự kiến, xem [mục 13](#13-xử-lý-sự-cố) trước khi deploy.

Bấm **Deploy**. Lần deploy đầu làm theo thứ tự:

1. Ghi compose file (cùng tên với file gốc) và `.env`. `.env` có thêm một block `# ---- managed by v-panel ----` ở đầu (`COMPOSE_PROJECT_NAME`, `VPANEL_*`); phần còn lại là `.env` của stack.
2. Không sinh `docker-compose.vpanel.yml` khi proxy service đã có alias trên shared network.
3. `docker compose config`, rồi `pull` theo chế độ Images, rồi `up -d --remove-orphans` với **cùng tên project**.
4. Ghi `sites/vp-<host>.caddy` và gỡ site viết tay trong **cùng một lần** `caddy validate` + `caddy reload`.
   - File viết tay chỉ còn comment thì được chuyển hẳn vào `/opt/infra/caddy/.vpanel-backup/<thời điểm>/`.
   - File còn site khác thì chỉ gỡ đúng site đó (file gốc vẫn được backup).
   - Validate hoặc reload lỗi thì **khôi phục cả hai** và job báo lỗi.

**Kiểm tra sau deploy:**

```bash
curl -kI https://internal.n8n-arm.arm-app-u01/
```

```bash
cd /opt/infra && docker compose exec caddy caddy validate --config /etc/caddy/Caddyfile
```

```bash
docker volume ls > ~/after-volumes.txt && diff ~/before-volumes.txt ~/after-volumes.txt
```

Lệnh `diff` không được ra dòng nào: không có volume mới. Tab **History** của instance có log đầy đủ của job; chip "imported" biến mất.

## 10. Ví dụ đầy đủ: arm-app-u01

1. `sudo ./deploy/docker-install.sh --adopt --pull`. Script tìm ra container `infra-caddy-1`, network `proxy` và các root `/opt/cxml-dev-server`, `/opt/infra`, `/opt/n8n-arm`.
2. **Import > Scan server**:
   - Infra: shared network `proxy`, container `infra-caddy-1`, thư mục `/opt/infra/caddy`, ingress `Direct`, PostgreSQL `postgres` trên `db` với alias `infra-postgres`.
   - Site: `internal.n8n-arm.arm-app-u01` → `n8n-arm:5678`, `internal.cxml.arm-app-u01` → `cxml-app:8080`, cả hai "yes, keeping its directives".
3. Import `infra`. Caddyfile đã có `import sites/*.caddy` nên không bị sửa.
4. Import `n8n-arm`: Public URL `https://internal.n8n-arm.arm-app-u01`, service `n8n`, port 5678; `tls internal` tắt (TLS lấy từ snippet `common`); Images = Build. Preview cho `n8n: keep`. Deploy sinh ra `sites/vp-internal.n8n-arm.arm-app-u01.caddy`:

   ```caddyfile
   # Managed by v-panel. Do not edit: regenerated on every deploy.
   # route n8n-arm -> n8n-arm:5678
   internal.n8n-arm.arm-app-u01 {
   	import common

   	header {
   		X-Content-Type-Options "nosniff"
   		Referrer-Policy "strict-origin-when-cross-origin"
   		-Server
   	}

   	# n8n uses WebSockets for the editor (push); reverse_proxy passes them through.

   	reverse_proxy n8n-arm:5678
   }
   ```

   File `sites/n8n-arm.caddy` được chuyển vào `/opt/infra/caddy/.vpanel-backup/…/sites/n8n-arm.caddy`.
5. Import `cxml-dev-server`: service `app`, port 8080; Images = **Pull first** (pull `ghcr.io/mthpbs/cxml-dev-server`, `backup:local` chỉ build khi thiếu). Phần reverse_proxy options giữ health check:

   ```caddyfile
   	reverse_proxy cxml-app:8080 {
   		health_uri /health
   		health_interval 15s
   		health_timeout 5s
   	}
   ```

## 11. Vận hành sau migration

- **Sửa trên v-panel, không sửa tay** compose/`.env` trong thư mục stack: mỗi lần deploy v-panel ghi đè chúng từ template và override. Sửa compose ở trang app; sửa env, Public URL, proxy, directives ở trang instance.
- File `vp-*.caddy` cũng được sinh lại mỗi lần deploy. Muốn đổi header hay health check thì sửa **Caddy site directives** / **reverse_proxy options** trên instance rồi deploy.
- Snippet trong Caddyfile chính (ví dụ `(common)`) vẫn do anh quản lý. Sửa snippet thì reload Caddy như trước; site của v-panel dùng snippet đó cũng nhận thay đổi.
- Site viết tay khác (không take over) vẫn để nguyên. Đừng tạo thêm một site viết tay trùng host với một route của v-panel: Caddy sẽ từ chối toàn bộ cấu hình.
- Lệnh `docker compose` chạy tay trong thư mục stack vẫn dùng được (cùng file, cùng project), nhưng mọi thay đổi cấu hình phải đưa vào v-panel, nếu không lần deploy sau sẽ ghi đè.

## 12. Rollback

**Trước lần deploy đầu:** menu **⋮ > Stop managing (keep containers)**. Không có gì cần khôi phục: v-panel mới chỉ tạo `.vpanel-backup/` (và `.env` nếu trước đó chưa có).

**Sau khi đã deploy** (ví dụ với n8n; làm đúng thứ tự):

1. Gỡ instance khỏi v-panel: **⋮ > Remove deployment…**, **bỏ chọn** cả hai ô xoá volume và xoá thư mục. Job này chạy `docker compose down` (container dừng, volume và file giữ nguyên) và đồng bộ lại route, nên v-panel tự xoá file `sites/vp-internal.n8n-arm.arm-app-u01.caddy`. Sau đó chọn **⋮ > Delete instance**.
2. Khôi phục file của stack từ bản backup lúc import:

   ```bash
   cd /opt/n8n-arm && ls .vpanel-backup/
   ```

   ```bash
   sudo cp .vpanel-backup/<thời điểm>/docker-compose.yml .vpanel-backup/<thời điểm>/.env .
   ```

3. Khôi phục site Caddy viết tay rồi reload:

   ```bash
   sudo cp /opt/infra/caddy/.vpanel-backup/<thời điểm>/sites/n8n-arm.caddy /opt/infra/caddy/sites/
   ```

   ```bash
   cd /opt/infra && docker compose exec caddy caddy reload --config /etc/caddy/Caddyfile
   ```

4. Cho stack chạy lại bằng file gốc:

   ```bash
   cd /opt/n8n-arm && docker compose up -d
   ```

Phải gỡ instance (bước 1) **trước** khi khôi phục site viết tay. Nếu làm ngược lại, lần đồng bộ route tiếp theo sẽ sinh lại file `vp-*` trùng host và Caddy từ chối cấu hình.

**Gỡ một thư mục khỏi adopted roots:** chạy lại installer với `--adopt` và danh sách mới.

**Gỡ hẳn v-panel:**

```bash
docker compose -f deploy/docker-compose.yml -f /opt/vpanel/docker-compose.adopt.yml --env-file /opt/vpanel/vpanel.env down
```

Các stack đã khôi phục ở trên tiếp tục chạy độc lập.

## 13. Xử lý sự cố

| Triệu chứng | Nguyên nhân | Cách xử lý |
|---|---|---|
| Nút Import của app bị mờ, ghi "Import the shared infrastructure first" | Shared network trong Settings khác network của Caddy | Import stack infra trước |
| Import báo `… is outside the apps root and the adopted roots` | Thư mục chưa nằm trong `AdoptedRoots` | Chạy lại installer với `--adopt <root>` |
| Import báo `… served by a hand-written Caddy site … cannot take over` | Site có định tuyến | Để trống Public URL; site giữ nguyên là file viết tay |
| Preview báo `recreate` dù chưa sửa gì | Compose gốc khác với lúc container được tạo (đã sửa file mà chưa `up`), hoặc proxy service chưa có alias trên shared network (v-panel sẽ thêm alias) | Xem lại compose; nếu muốn tránh recreate thì thêm `aliases:` trên network dùng chung vào compose của stack (sửa ở trang app) |
| Deploy lỗi `Caddy validation failed … (restored)` | Directive không hợp lệ, snippet không tồn tại, hoặc trùng host với site viết tay khác | Đọc log job, sửa directives/site; file đã được khôi phục |
| Pull báo lỗi với image `…:local` | Image chỉ build ở local | Dùng Images = Pull first (lỗi pull được bỏ qua, image thiếu sẽ được build) |
| `couldn't find env file …/.env` | Stack không có `.env` | Import sẽ tự tạo; nếu đã xoá thì tạo file `.env` rỗng trong thư mục stack |
| `Cannot write a backup into …` | Container vpanel không ghi được vào thư mục | Kiểm tra mount (mục 4) và quyền thư mục |
| Check Caddy báo không exec được | Tên container Caddy sai (thường gặp khi Caddy không chạy lúc scan) | Sửa **Settings > Caddy container name** |

## 14. Làm bằng AI agent (MCP)

Cùng quy trình trên, qua MCP ([docs/mcp.md](mcp.md)):

1. `scan_existing_stacks`: kết quả giống trang Import, kèm `importInfraFirst` và `canImport` cho từng stack.
2. `import_stack(project: "infra")`, rồi `import_stack(project: "n8n-arm")`. Tham số không truyền thì dùng giá trị gợi ý từ scan (Public URL, proxy, `proxy_directives`, `proxy_options`, `build_mode`).
3. `preview_deploy(instance: "n8n-arm")`.
4. `run_instance_action(instance: "n8n-arm", action: "deploy")`.

Hướng dẫn gửi kèm MCP yêu cầu agent hỏi anh trước khi deploy nếu preview có `recreate`.

## 15. Giới hạn

- **Caddy chỉ mount một file Caddyfile** (`./Caddyfile:/etc/caddy/Caddyfile`): file `vp-*.caddy` sẽ không vào được container. Đổi sang mount cả thư mục (`./caddy:/etc/caddy:ro`), recreate Caddy, rồi scan lại. v-panel chỉ cảnh báo, không tự sửa compose của infra.
- **Nhiều compose file** (`-f a.yml -f b.yml`, hoặc `compose.override.yml`): được merge bằng `docker compose config --no-interpolate` thành `docker-compose.vpanel-merged.yml`, file gốc giữ nguyên. Comment bị mất, và đường dẫn tương đối của file nằm ở thư mục khác có thể sai: kiểm tra kỹ bằng Preview.
- **Env file không tên `.env`**: nội dung được import và v-panel ghi ra `.env`.
- **Site Caddy có định tuyến** (`handle`, `handle_path`, `route`, `redir`, `rewrite`, `respond`, `file_server`, matcher `@…`, `reverse_proxy` có matcher hoặc `to`): không take over được.
- **Container không chạy bằng docker compose** (`docker run`): không được scan.
- **Reverse proxy khác Caddy**: không hỗ trợ.
- **Một server**: v-panel quản lý Docker của chính server nó đang chạy.
