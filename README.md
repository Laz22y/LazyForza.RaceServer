# LazyForza RaceServer

<p align="center"><a href="#简体中文">简体中文</a> · <a href="#english">English</a></p>

## 简体中文

LazyForza 地产赛事的独立服务端。支持原生 ASP.NET 自托管和 Cloudflare Durable Objects，两套实现保持同一协议与 Web 总控功能。

0.4.3 为 Web 总控加入完整中英文界面，并改善弱网广播隔离、实时秒差、换胎停留确认和疑似违规操作区布局。总控语言由各浏览器独立保存，不影响房间协议或客户端语言。

[客户端下载](https://github.com/Laz22y/LazyForza/releases/latest) · [完整文档](https://laz22y.github.io/LazyForza/docs/#race-server) · [服务端 Releases](https://github.com/Laz22y/LazyForza.RaceServer/releases/latest)

## 提供什么

- 1–12 名车手，可单人发车；额外支持最多 12 个只读 OB 席位；
- 1–3 节练习与排位、正赛、出场圈、暖胎圈、五盏红灯和方格旗；
- 车队、维修区、旗语、处罚、带动态遥测回放的全阶段碰撞调查、路线收益切弯证据、DNF/DSQ 和可选断线计圈恢复；
- 赛道文件与主办方 Logo 托管；
- 可保存、覆盖、应用和删除的赛事规则模板，赛事名称、赛道与车队资料保持独立；
- 可创建、更新、复制、启用、完成和归档的赛事项目；`.lfzevent` 项目包可携带房间规则、赛程、车队、赛道、Logo、阶段赛果与赛事记录，在原生和 Cloudflare 服务端之间迁移；
- 阶段赛果归档，返回大厅后仍可回看，并支持 PNG/CSV 导出；
- 面向电脑宽屏和 Pad 触控的浏览器总控；
- JSONL 审计日志和关键赛事状态持久化。

服务端不读取游戏。车手位置、圈速、维修区状态和抓地趋势由各自的 LazyForza 客户端通过 FH6 官方 UDP 推导后上传。

## 选择部署方式

| 方式 | 适合场景 | 发行包 |
| --- | --- | --- |
| 原生自托管 | 本地联机、VPS、固定服务器 | Windows、Linux x64/ARM64、macOS x64/ARM64 |
| Cloudflare Durable Objects | 不想维护 VPS，接受 Cloudflare 平台 | Cloudflare 源码包或仓库模板 |

## 原生服务端

从 [Releases](https://github.com/Laz22y/LazyForza.RaceServer/releases/latest) 下载对应平台 ZIP，解压后运行：

```powershell
# Windows
./LazyForza.RaceServer.Web.exe
```

```bash
# Linux / macOS
chmod +x ./LazyForza.RaceServer.Web
./LazyForza.RaceServer.Web
```

默认监听 `http://0.0.0.0:24876`。首次打开网页时设置房间密码、总控密码和赛事基础规则。房间密码没有最少位数限制；总控密码需 8–128 个字符，且不能与房间密码相同。

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

需要 .NET SDK 9。Cloudflare 端另需 Node.js 20+：

第一次接触仓库或修改跨端行为时，先读 [Coding Agent 开发入口](AGENTS.md)。其中列出协议三份副本、原生/Cloudflare 对等文件和验证矩阵。

```powershell
dotnet restore LazyForza.RaceServer.sln
dotnet build LazyForza.RaceServer.sln -c Release --no-restore
dotnet test LazyForza.RaceServer.sln -c Release --no-build --no-restore

cd cloudflare
npm ci
npm run check
npm test
npm run dry-run
```

运行原生端：

```powershell
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
- JSONL audit logs and persistent critical race state.

The server does not read the game. Each LazyForza client derives local position, lap, pit and grip information from official FH6 UDP data and reports the required race data.

### Deployment options

| Option | Best for | Release package |
| --- | --- | --- |
| Native self-hosting | LAN races, VPS or dedicated servers | Windows, Linux x64/ARM64 and macOS x64/ARM64 |
| Cloudflare Durable Objects | Hosting without maintaining a VPS | Cloudflare source package or repository template |

### Native server

Download the ZIP for your platform from [Releases](https://github.com/Laz22y/LazyForza.RaceServer/releases/latest), extract it and run:

```powershell
# Windows
./LazyForza.RaceServer.Web.exe
```

```bash
# Linux / macOS
chmod +x ./LazyForza.RaceServer.Web
./LazyForza.RaceServer.Web
```

The server listens on `http://0.0.0.0:24876` by default. On first launch, set the room password, Race Control password and base rules. The Race Control password must contain 8–128 characters and must differ from the room password.

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

Requires .NET SDK 9; the Cloudflare implementation also requires Node.js 20+:

```powershell
dotnet restore LazyForza.RaceServer.sln
dotnet build LazyForza.RaceServer.sln -c Release --no-restore
dotnet test LazyForza.RaceServer.sln -c Release --no-build --no-restore

cd cloudflare
npm ci
npm run check
npm test
npm run dry-run
```

Protocol, Race Control API and web changes must be implemented and tested in both the native and Cloudflare versions. Read [AGENTS.md](AGENTS.md) for the contract map and validation matrix.

### Data boundaries

- Native state, settings and audit logs are stored under `data`; Cloudflare uses Durable Object storage.
- The server cannot confirm that the game actually changed tires or repaired damage.
- Deterministic multi-client tests do not replace real FH6 multi-PC, public-network or Cloudflare deployment validation.

### License

[MIT](LICENSE). LazyForza RaceServer is an unofficial community project not affiliated with Microsoft, Xbox or Playground Games.
