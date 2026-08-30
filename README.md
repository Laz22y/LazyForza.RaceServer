# LazyForza RaceServer

<p align="center"><a href="#简体中文">简体中文</a> · <a href="#english">English</a></p>

## 简体中文

LazyForza 地产赛事的独立服务端。支持原生 ASP.NET 自托管和 Cloudflare Durable Objects，两套实现保持同一协议与 Web 总控功能。

0.4.3 为 Web 总控加入完整中英文界面，并改善弱网广播隔离、实时秒差、换胎停留确认和疑似违规操作区布局。总控语言由各浏览器独立保存，不影响房间协议或客户端语言。

[客户端下载](https://github.com/Laz22y/LazyForza/releases/latest) · [完整文档](https://laz22y.github.io/LazyForza/docs/#race-server) · [服务端 Releases](https://github.com/Laz22y/LazyForza.RaceServer/releases/latest)

## 提供什么

- 1–12 名车手，可单人发车；额外支持最多 12 个只读 OB 席位；
- 1–3 节练习与排位、正赛、出场圈、暖胎圈、五盏红灯和方格旗；
- 发车前检查车手连接、准备、遥测、维修区、赛道与规则状态；检查结果仅警告，总控确认后可强制启动；
- 车队、维修区、旗语、处罚、带动态遥测回放的全阶段碰撞调查、路线收益切弯证据、DNF/DSQ 和可选断线计圈恢复；
- 赛道文件与主办方 Logo 托管；
- 可保存、覆盖、应用和删除的赛事规则模板，赛事名称、赛道与车队资料保持独立；
- 可创建、更新、复制、启用、完成和归档的赛事项目；`.lfzevent` 项目包可携带房间规则、赛程、车队、赛道、Logo、阶段赛果与赛事记录，在原生和 Cloudflare 服务端之间迁移；
- 阶段赛果归档，返回大厅后仍可回看，并支持 PNG/CSV 导出；
- 面向电脑宽屏和 Pad 触控的浏览器总控；
- 可为多名总控分别创建账号：超管拥有全部权限，管理员可管理赛事但不能管理总控账号，裁判仅处理判罚与调查；
- 独立只读令牌保护的公开实时计时页，展示排名、圈数、Delta、最佳圈、旗语、维修、处罚和阶段赛果，适配手机、Pad 与直播浏览器源；
- JSONL 审计日志和关键赛事状态持久化。

服务端不读取游戏。车手位置、圈速、维修区状态和抓地趋势由各自的 LazyForza 客户端通过 FH6 官方 UDP 推导后上传。

## 选择部署方式

| 方式 | 适合场景 | 发行包 |
| --- | --- | --- |
| 原生自托管 | 本地联机、VPS、固定服务器 | Windows、Linux x64/ARM64、macOS x64/ARM64 |
| Cloudflare Durable Objects | 不想维护 VPS，接受 Cloudflare 平台 | Cloudflare 源码包或仓库模板 |

## 原生服务端

从 [Releases](https://github.com/Laz22y/LazyForza.RaceServer/releases/latest) 下载对应平台 ZIP。全新服务器先在服务器终端执行一次初始化，再启动服务：

```powershell
# Windows
./LazyForza.RaceServer.Web.exe init
./LazyForza.RaceServer.Web.exe
```

```bash
# Linux / macOS
chmod +x ./LazyForza.RaceServer.Web
./LazyForza.RaceServer.Web init
./LazyForza.RaceServer.Web
```

`init` 会在本机终端中询问房间密码、初始超级管理员密码、赛事名称、正赛圈数和分段数；密码输入不回显，只以现有 PBKDF2 摘要格式写入 `data/server-settings.json`。已经存在有效配置时，`init` 会拒绝覆盖。未初始化时直接启动服务会在监听任何 HTTP / WebSocket 端口前报错退出，因此不能在浏览器或远程 API 中完成原生端首次设置。

初始化后，服务默认监听 `http://0.0.0.0:24876`。超管登录网页 Race Control 后可按需创建多个超管、管理员或裁判账号，同一角色可有多个独立名称和密码。房间密码没有最少位数限制；总控账号密码需 8–128 个字符，且不能与房间密码或其他总控账号密码相同。升级时会直接读取现有 `data/server-settings.json`，不要求重新初始化。

管理员或超管可在“公开实时计时”区生成普通浏览和直播透明背景链接。只读令牌与总控账号独立，明文只在生成时显示；轮换或停用后，旧链接立即失效。

公网部署应由 Caddy、Nginx 或同类反向代理终止 TLS，让客户端连接 `wss://`。不要直接暴露明文 `ws://`。

## Cloudflare Durable Objects

[![Deploy to Cloudflare](https://deploy.workers.cloudflare.com/button)](https://deploy.workers.cloudflare.com/?url=https://github.com/Laz22y/LazyForza.RaceServer/tree/main/cloudflare)

也可以在仓库根目录运行：

```powershell
./scripts/Deploy-Cloudflare.ps1
```

需要 Node.js 20+、npm 和 PowerShell 7。部署后打开 Worker 域名完成首次设置，再在总控页上传 `.lfzestate` 赛道文件。详细参数见 [cloudflare/README.md](cloudflare/README.md)。

## 客户端连接

车手需要服务端域名或 IP、房间密码、匹配的地产赛道、显示名和可选车队。WebSocket 路径为 `/ws`。OB 使用 OB 身份登录，只接收赛事快照，不上传遥测、不参与排名和处罚。

总控可上传不超过 1.5 MiB 的 `.lfzestate`。服务端校验文件清单与 SHA-256；客户端缺少匹配赛道时，由车手确认下载并再次校验。

## 兼容性

当前正式服务端为 `v0.4.3`：

- LazyForza `1.5.0`：推荐版本，完整支持中英文客户端、弱网隔离、稳定实时秒差和可靠换胎停留确认；
- LazyForza `1.4.9`：支持路线收益切弯证据、碰撞识别和维修区轨迹保护；
- LazyForza `1.4.8`：完整支持弱网状态提示与可选断线计圈恢复；
- LazyForza `1.4.7`：支持增强碰撞证据与其发布时的全部赛事交互；
- LazyForza `1.4.2–1.4.6`：协议 v2 主要赛事流程兼容，但不具备其版本发布后新增的全部客户端能力；
- LazyForza `1.4.1` 及更早版本没有当前地产赛事客户端，不列入支持范围。

## 本地开发

需要 .NET SDK 9 和 Node.js 20+；构建时会从 Schema 与原生 Web 源生成跨端产物：

第一次接触仓库或修改跨端行为时，先读 [Coding Agent 开发入口](AGENTS.md)。其中列出单一协议 Schema、三端生成文件、原生/Cloudflare 对等实现和验证矩阵。

```powershell
dotnet restore LazyForza.RaceServer.sln
dotnet build LazyForza.RaceServer.sln -c Release --no-restore
dotnet test LazyForza.RaceServer.sln -c Release --no-build --no-restore

cd cloudflare
npm ci
npm run check:generated
npm run check
npm test
npm run dry-run
```

运行原生端：

```powershell
dotnet run --project src/LazyForza.RaceServer.Web/LazyForza.RaceServer.Web.csproj -- init
dotnet run --project src/LazyForza.RaceServer.Web/LazyForza.RaceServer.Web.csproj
```

任何协议、总控接口或 Web 功能变更都必须同步修改原生端与 Cloudflare 端，并补齐双端测试。协议还需要同步 LazyForza 客户端；完整文件映射见 [AGENTS.md](AGENTS.md)。

## 数据与验证边界

- 原生端将设置、赛事快照和审计日志保存在 `data`；Cloudflare 端使用 Durable Object 存储；
- 服务端不判断游戏是否真的更换轮胎，也不伪造 FH6 未提供的数据；
- 4–12 人和 OB 已有确定性自动测试覆盖；真实 FH6 多机联机结论必须单独记录，不能用模拟器或自动测试替代。

## License

[MIT](LICENSE)。LazyForza RaceServer 是非官方社区项目，与 Microsoft、Xbox 或 Playground Games 无隶属关系。

## English

LazyForza RaceServer is the independent server for estate racing. Native ASP.NET self-hosting and Cloudflare Durable Objects provide the same protocol, race behavior and browser Race Control.

Version 0.4.3 adds a complete Chinese and English Race Control interface, isolates slow connections from room broadcasts, stabilizes live gaps, improves tire-change dwell confirmation and fixes overlap in non-collision investigation controls. Each browser stores its own interface language; room protocol and client language remain independent.

[Client downloads](https://github.com/Laz22y/LazyForza/releases/latest) · [Documentation](https://laz22y.github.io/LazyForza/docs/#race-server) · [Server releases](https://github.com/Laz22y/LazyForza.RaceServer/releases/latest)

### Features

- 1–12 drivers with single-driver starts, plus up to 12 read-only observer slots;
- one to three practice and qualifying sessions, races, out laps, formation laps, five red lights and the checkered flag;
- teams, pit lanes, flags, penalties, collision investigations with dynamic telemetry replay, shortcut evidence, DNF/DSQ and optional disconnected-lap recovery;
- hosted track packages and organizer logos;
- reusable race-rule templates that keep event, track and team details separate;
- reusable event projects with create, update, copy, activate, complete and archive workflows; `.lfzevent` packages carry room rules, schedules, teams, track, logo, session results and race logs between native and Cloudflare servers;
- archived session results that remain available in the lobby, with PNG and CSV export;
- browser Race Control designed for desktop widescreens and touch tablets;
- warning-only pre-race checks for driver connectivity, readiness, telemetry, pit state, track identity and race rules, with an explicit force-start action;
- separate Race Control accounts for multiple users: super admins have full access, administrators manage the race but not accounts, and stewards handle penalties and investigations only;
- public live timing protected by a separate read-only token, with standings, laps, deltas, best laps, flags, pit state, penalties and stage results for phones, tablets and broadcast browser sources;
- JSONL audit logs and persistent critical race state.

The server does not read the game. Each LazyForza client derives local position, lap, pit and grip information from official FH6 UDP data and reports the required race data.

### Deployment options

| Option | Best for | Release package |
| --- | --- | --- |
| Native self-hosting | LAN races, VPS or dedicated servers | Windows, Linux x64/ARM64 and macOS x64/ARM64 |
| Cloudflare Durable Objects | Hosting without maintaining a VPS | Cloudflare source package or repository template |

### Native server

Download the ZIP for your platform from [Releases](https://github.com/Laz22y/LazyForza.RaceServer/releases/latest). On a new server, initialize it once in the server terminal before starting the service:

```powershell
# Windows
./LazyForza.RaceServer.Web.exe init
./LazyForza.RaceServer.Web.exe
```

```bash
# Linux / macOS
chmod +x ./LazyForza.RaceServer.Web
./LazyForza.RaceServer.Web init
./LazyForza.RaceServer.Web
```

The `init` command prompts locally for the room password, initial Super Admin password, event name, race laps and sector count. Password input is not echoed and only the existing PBKDF2 digest format is written to `data/server-settings.json`. The command refuses to overwrite valid existing credentials. Starting an uninitialized server exits with an error before any HTTP or WebSocket port is opened, so native initial setup cannot be completed in a browser or through a remote API.

After initialization, the server listens on `http://0.0.0.0:24876` by default. A Super Admin can sign in to Race Control and create multiple named Super Admin, administrator or steward accounts, including several users with the same role. Each Race Control password must contain 8–128 characters and must differ from the room password and every other Race Control password. Upgrades continue to use an existing `data/server-settings.json` without requiring initialization again.

Administrators and super admins can generate regular viewer and transparent broadcast links from the Public Live Timing panel. The read-only token is independent of Race Control accounts and is shown only when generated; rotating or disabling it invalidates every previous link immediately.

For public hosting, terminate TLS through Caddy, Nginx or a similar reverse proxy and connect clients over `wss://`. Do not expose plain `ws://` publicly.

### Cloudflare Durable Objects

[![Deploy to Cloudflare](https://deploy.workers.cloudflare.com/button)](https://deploy.workers.cloudflare.com/?url=https://github.com/Laz22y/LazyForza.RaceServer/tree/main/cloudflare)

Or run from the repository root:

```powershell
./scripts/Deploy-Cloudflare.ps1
```

Requires Node.js 20+, npm and PowerShell 7. Open the Worker domain after deployment to finish setup, then upload a `.lfzestate` track package from Race Control. See [cloudflare/README.md](cloudflare/README.md) for deployment details.

### Client connection

Drivers need the server domain or IP, room password, matching estate circuit, display name and optional team. The WebSocket endpoint is `/ws`. Observers receive race snapshots only and do not upload telemetry or participate in standings or penalties.

Race Control accepts `.lfzestate` packages up to 1.5 MiB. The server verifies the manifest and SHA-256; clients without the matching track confirm the download and verify it again.

### Compatibility

RaceServer `0.4.3` is recommended with LazyForza `1.5.0`. The main protocol v2 race flow remains compatible with LazyForza `1.4.2–1.4.9`; features introduced after a client version are unavailable to that older client. Disconnected-lap recovery requires client `1.4.8` or later and must be enabled from Race Control.

### Local development

Requires .NET SDK 9 and Node.js 20+ because builds generate cross-target artifacts from the schema and native web source:

```powershell
dotnet restore LazyForza.RaceServer.sln
dotnet build LazyForza.RaceServer.sln -c Release --no-restore
dotnet test LazyForza.RaceServer.sln -c Release --no-build --no-restore

cd cloudflare
npm ci
npm run check:generated
npm run check
npm test
npm run dry-run
```

Protocol models are generated from the single [protocol schema](protocol/race-protocol.schema.json). Protocol behavior, Race Control APIs and web changes must still be implemented and tested in both the native and Cloudflare versions. Read [AGENTS.md](AGENTS.md) for the contract map and validation matrix.

To initialize and then run the native development server:

```powershell
dotnet run --project src/LazyForza.RaceServer.Web/LazyForza.RaceServer.Web.csproj -- init
dotnet run --project src/LazyForza.RaceServer.Web/LazyForza.RaceServer.Web.csproj
```

### Data boundaries

- Native state, settings and audit logs are stored under `data`; Cloudflare uses Durable Object storage.
- The server cannot confirm that the game actually changed tires or repaired damage.
- Deterministic multi-client tests do not replace real FH6 multi-PC, public-network or Cloudflare deployment validation.

### License

[MIT](LICENSE). LazyForza RaceServer is an unofficial community project not affiliated with Microsoft, Xbox or Playground Games.
