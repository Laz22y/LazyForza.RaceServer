# Cloudflare Durable Objects 部署

<p align="center"><a href="#简体中文">简体中文</a> · <a href="#english">English</a></p>

## 简体中文

开发或修改 Cloudflare 实现前先读仓库根目录 [`AGENTS.md`](../AGENTS.md)。Cloudflare 与原生 ASP.NET 是同一服务端的两套实现，对客户端可见的协议、比赛行为、管理接口和 Web 总控必须保持一致。

这个目录提供与 LazyForza 地产赛事客户端协议 v2 兼容的 Cloudflare Workers + Durable Objects 服务端。一个 Worker 固定使用一个名为 `main` 的赛事房间，支持 1–12 名车手，并可额外连接最多 12 个只读 OB 席位。OB 不占车手名额，可在比赛进行中加入，只接收赛事数据用于观赛或转播。

正式服务端 `v0.4.3` 推荐搭配 LazyForza `1.5.0`，并与 `1.4.2`–`1.4.9` 的协议 v2 主要比赛流程兼容。断线计圈恢复需要 `1.4.8` 或更高版本，并由总控主动开启。旧客户端不会使用其版本发布后新增的练习项目、进站策略预测、OB 登录、主办方 Logo、赛道文件按需下载和后续维修区路线修正；1.4.2 没有服务端车队下拉框，填写名称能匹配时按名称加入，否则由服务端自动分配空余车队。完整兼容说明见仓库根目录 `README.md`。

实现范围：

- 比赛密码、总控密码、显示名、主题色、可选车队和断线恢复；
- 由总控选择开启的 30 秒断线计圈恢复，补交事件支持确认和去重；
- 1–3 节练习赛、每节默认 60 分钟或由总控逐节设置、独立圈速排名和最后一圈收尾；
- 可保存、覆盖、应用和删除的赛事规则模板，赛事名称、赛道与车队资料保持独立；
- 大厅、兼容单节的 1–3 节排位、默认或自定义淘汰、每节最后飞驰圈、完整排位顺位、出场圈和暖胎圈；
- 五盏红灯随机熄灭发车、抢跑自动加罚、正赛、红旗暂停和自动方格旗；
- 全场最快圈、排名、排位/正赛自动黄旗、人工分区/全场黄旗、自动蓝旗、处罚、DNF/DSQ、维修停留进度；
- 练习、排位与正赛均可生成碰撞待审调查；
- 接收客户端路线收益切弯证据，并保持正赛首圈、换位和进出维修区后的实时秒差连续；
- WebSocket Hibernation，空闲连接不要求 Worker 一直驻留；
- SQLite 后端 Durable Object 保存关键赛事状态；
- 复用 .NET 自托管版的 Web 总控静态页面。
- 与自托管版一致的判罚/调查区、赛后加时结算及处罚修改和取消接口。

客户端遥测默认 10 Hz，但房间快照广播最多 10 Hz。服务端不采信遥测消息中的累计圈数，只有唯一且有效的 `lapCompleted` 事件能把服务端权威圈数增加一圈。维修区只记录停留条件和次数，不能证明游戏已经更换轮胎或重置车损。

## 网页一键部署

点击下面的按钮，登录自己的 Cloudflare 账号并确认创建 Worker 与 Durable Object：

[![Deploy to Cloudflare](https://deploy.workers.cloudflare.com/button)](https://deploy.workers.cloudflare.com/?url=https://github.com/Laz22y/LazyForza.RaceServer/tree/main/cloudflare)

Cloudflare 会把这个公开模板复制到你的 GitHub 或 GitLab 账号，自动创建 Durable Object 绑定并配置后续提交的构建部署。`cloudflare` 子目录已包含 Worker、依赖锁文件和控制面板静态资源，不依赖仓库上级目录。

部署完成后直接打开 Cloudflare 分配的域名。网页第一次打开只需要设置密码和房间基础规则，不要求填写赛道文件信息；房间密码没有最少位数限制，总控密码仍需 8–128 个字符，密码只以加盐摘要保存在 Durable Object 中。初始化完成后，到总控页面上传 LazyForza 导出的 `.lfzestate`，服务端会自动识别并填写赛道名称、标识、地图修订和稳定特征值。完成设置后，把域名与房间密码发给车手，总控密码只由赛事管理员保留。

## PowerShell 部署

需要 Node.js 20 或更高版本、npm 和 PowerShell 7。在仓库根目录运行：

```powershell
./scripts/Deploy-Cloudflare.ps1
```

脚本会安装锁定依赖、执行 TypeScript 检查和单元测试、打开 Cloudflare 授权登录，并以隐藏输入方式读取密码。填写车手密码时会预设两个 Secret；留空则不预设密码，部署后需要立即打开网页完成首次设置。密码不会写入仓库或配置文件，也不会由脚本输出。

可以指定独立 Worker 名称：

```powershell
./scripts/Deploy-Cloudflare.ps1 -WorkerName lazyforza-my-race
```

首次部署后，Wrangler 会输出 `workers.dev` 地址。这个脚本会预先写入两组 Secret，因此打开网页后可以直接使用总控密码登录，不会再次出现首次设置页。

## 初始参数与赛道锁定

编辑 `wrangler.jsonc` 的 `vars`：

- `MAXIMUM_PARTICIPANTS`：服务端仍会强制限制在 1–12；
- `TOTAL_RACE_LAPS`：初始正赛圈数，可在总控中保存修改；
- 一键部署和首次初始化都不要求赛道文件信息；初始化后在网页总控填写即可；
- LazyForza 的“赛事信息”按钮以及 `.lfzestate` 导出完成窗口会显示可复制的赛道名称、标识和稳定特征 SHA-256，方便人工核对；
- 总控直接上传对应 `.lfzestate` 即可，Durable Object 会校验包内摘要并自动配置房间赛道，托管上限为 1.5 MiB；已有匹配赛道的客户端只读取房间描述，不下载文件，缺少或特征不一致时才由车手确认是否下载；
- 客户端会在导入前再次校验包内清单与 SHA-256。Cloudflare 端保存的文件不会混入实时 WebSocket 遥测或房间快照；
- `SERVER_NAME`、`SESSION_NAME`：服务名和初始赛事名。

修改非 Secret 配置后重新运行部署脚本即可。更新密码可单独执行：

```powershell
cd cloudflare
npx wrangler secret put PLAYER_PASSWORD
npx wrangler secret put ADMIN_PASSWORD
```

## 本地验证

```powershell
cd cloudflare
npm ci
npm run check
npm test
npm run dry-run
npm run dev
```

本地测试和 dry-run 不等于中国大陆网络直连或真实 FH6 多机联机验证。实际使用前仍应从参赛车手所在网络测试 HTTPS、WebSocket 握手、10 Hz 状态更新和短时断线恢复。

## English

Read the repository-level [`AGENTS.md`](../AGENTS.md) before changing the Cloudflare implementation. Cloudflare Durable Objects and native ASP.NET are equal RaceServer targets and must keep the same client protocol, race behavior, management API and Race Control features.

RaceServer `0.4.3` is recommended with LazyForza `1.5.0` and remains compatible with the main protocol v2 race flow in LazyForza `1.4.2–1.4.9`. Disconnected-lap recovery requires client `1.4.8` or later and must be explicitly enabled from Race Control.

The Worker uses one Durable Object race room named `main`, with 1–12 drivers and up to 12 read-only observers. It supports:

- room and Race Control passwords, display names, colors, teams and reconnect recovery;
- reusable race-rule templates that keep event, track and team details separate;
- one to three practice and qualifying sessions, out laps, formation laps, five red lights, races and red flags;
- standings, live gaps, flags, penalties, DNF/DSQ, collision investigations, shortcut evidence and pit progress;
- optional 30-second disconnected-lap recovery with acknowledged, deduplicated lap events;
- hosted `.lfzestate` track packages, organizer logos, WebSocket Hibernation and persistent Durable Object state;
- the same Chinese and English browser Race Control used by the native server.

Client telemetry defaults to 10 Hz and room snapshots are broadcast at no more than 10 Hz. Only a unique valid `lapCompleted` event advances the authoritative lap count. Pit state records location and dwell conditions; it cannot prove that the game changed tires or repaired damage.

### One-click deployment

[![Deploy to Cloudflare](https://deploy.workers.cloudflare.com/button)](https://deploy.workers.cloudflare.com/?url=https://github.com/Laz22y/LazyForza.RaceServer/tree/main/cloudflare)

Cloudflare copies this public template to your GitHub or GitLab account, creates the Durable Object binding and configures deployment from later commits. After deployment, open the Worker domain, complete first-time setup and upload the matching `.lfzestate` package from Race Control.

### PowerShell deployment

Requires Node.js 20+, npm and PowerShell 7. Run from the repository root:

```powershell
./scripts/Deploy-Cloudflare.ps1
```

Use a custom Worker name when needed:

```powershell
./scripts/Deploy-Cloudflare.ps1 -WorkerName lazyforza-my-race
```

The script installs locked dependencies, runs TypeScript checks and tests, opens Cloudflare authorization and reads passwords through hidden input. Passwords are never written to the repository or printed by the script.

### Configuration

Edit `wrangler.jsonc` for non-secret defaults:

- `MAXIMUM_PARTICIPANTS`: enforced between 1 and 12;
- `TOTAL_RACE_LAPS`: initial race length, editable from Race Control;
- `SERVER_NAME` and `SESSION_NAME`: server and initial event names.

Update secrets separately when required:

```powershell
cd cloudflare
npx wrangler secret put PLAYER_PASSWORD
npx wrangler secret put ADMIN_PASSWORD
```

Race Control accepts `.lfzestate` packages up to 1.5 MiB and verifies their manifest and SHA-256. Clients with a matching track do not download it; missing or mismatched tracks require driver confirmation and are verified again on import.

### Local validation

```powershell
cd cloudflare
npm ci
npm run check
npm test
npm run dry-run
npm run dev
```

Local tests and dry-runs do not prove public-network reachability or real FH6 multi-PC behavior. Test HTTPS, the WebSocket handshake, 10 Hz state updates and short reconnect recovery from the drivers' actual networks before an event.
