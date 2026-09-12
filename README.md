# LazyForza RaceServer

<p align="center"><a href="#简体中文">简体中文</a> · <a href="#english">English</a></p>

## 简体中文

预览版 `0.6.0-alpha-1` 推荐搭配 LazyForza `1.5.3-alpha-1`，新增原生重启恢复、双端圈完成校验和连接限速。协议保持 v2，新增字段为可选；旧客户端仍可连接，但不能提供新的阶段与校验证据。原生成功事件回执在持久保存后发送；重启恢复先等待管理员确认。旧版公开快照缺少身份和去重信息，不能安全续赛，升级前应备份并归档旧快照。

LazyForza 地产赛事的独立服务端。支持原生 ASP.NET 自托管和 Cloudflare Durable Objects，两套实现保持同一协议与 Web 总控功能。

0.5.0 新增令牌保护的公开实时计时、赛事规则模板、可迁移赛事项目、多角色总控账号和发车前检查；原生首次初始化改为仅限服务器终端，并恢复换胎出站后的实时 Delta。

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

### 原生重启恢复（当前源码）

原生版将内部恢复状态以独立格式版本 `1` 保存到 `data/current-race.json`。状态包含房间规则、阶段及计时、车手和 OB 恢复身份、成绩与分段、处罚执行状态、阶段赛果、调查与碰撞回放，以及圈完成和维修完成事件的去重集合。此文件包含恢复令牌，仅供服务器本地使用，不是公开计时快照或可分发的赛事项目包。

关键状态更改会同步保存；普通遥测通过约两秒一次的检查点保存。文件先写入同目录临时文件并刷新到磁盘，再原子替换完整状态。圈／维修的成功回执在该保存完成后发送，重复事件也必须经过保存确认。保存失败不会发送成功回执；未收到回执并不代表事件一定未被保存，客户端需使用原事件 ID 重试。JSONL 审计日志用于追溯，不作为恢复成绩的权威来源。

启动时在监听端口和运行赛事时钟前加载状态。进行中的赛事恢复为红旗暂停，车手连接标记为断开；管理员核对成绩、处罚和车手重连情况后，发布**全场绿旗**确认续赛，或返回大厅开始新赛事。等待确认期间不会自动推进计时、执行停车处罚或接受新的圈／维修事件；已保存事件的重复提交仍可确认。续赛会排除从最后检查点到确认时刻的停机等待时间，重新建立遥测连续性，尚未完成的停车执行重新计时，已执行处罚保持已执行。完成的赛事和大厅不自动进入新的比赛。

旧版 `current-race.json` 只有公开快照，缺少恢复令牌和去重信息，不能安全转换为可续赛状态。遇到旧版、损坏或未知版本文件时，启动会报错退出并保留原文件；旧版升级需先备份该快照用于核对成绩，再移走原文件以启动新赛事。未完成的临时文件不会替代最后完整检查点。旧版成功回执不具备可追溯补齐的完整恢复保证。

内部文件版本独立于协议 v2，无需新增 Schema 字段或客户端消息。Cloudflare 已通过 Durable Object storage 保存内部状态并在保存后确认事件，常规对象唤醒继续使用原有恢复流程，不套用原生进程重启的人工续赛等待。

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

## 入口保护（当前源码）

原生与 Cloudflare 版均限制登录失败窗口、未认证连接名额和每连接消息／字节预算。原生版使用 `RaceServer:Ingress` 配置。可通过 `appsettings.json` 的 `RaceServer.Ingress` 对象或 `RaceServer__Ingress__字段名` 环境变量设置；启动时拒绝无效配置。下表列出默认值：

| 字段 | 默认值 | 含义 |
| --- | --- | --- |
| LoginFailureLimit | 5 | 同来源、同登录身份的窗口额度 |
| SourceLoginFailureLimit | 120 | 同来源、同登录通道的总额度，限制轮换名字绕过 |
| LoginFailureWindowSeconds | 60 | 固定窗口；超限请求不延长锁定 |
| MaximumConcurrentLogins | 32 | 同时验证密码的上限 |
| MaximumFailureBuckets | 4096 | 有界失败记录数量；过期回收 |
| MaximumUnauthenticatedConnections | 64 | 全局尚未登录的 WebSocket 上限 |
| MaximumUnauthenticatedPerSource | 32 | 单来源尚未登录的 WebSocket 上限 |
| LoginTimeoutSeconds | 12 | 建立 WebSocket 后的登录期限 |
| MessagesPerSecond / MessageBurst | 60 / 120 | 每连接消息令牌补充速率与突发容量 |
| BytesPerSecond / ByteBurst | 131072 / 262144 | 每连接字节预算，包含分片数据 |
| TrustedProxyAddresses | 空数组 | 明确信任的直接反向代理 IP 地址 |

成功验证密码会归还登录额度；已认证连接不占未认证名额。玩家按来源加名字／恢复令牌摘要隔离，总控使用独立通道。少量输错密码不会阻断同一 NAT 下其他玩家或踢掉已连接玩家；来源总额度是短时极端滥用兜底，仍可能在同出口持续攻击时暂时限制新登录。不要把来源额度调到小于正常多人重连规模。应用限速不能替代公网反向代理／防火墙对分布式攻击的防护。

HTTP 登录或 WebSocket 握手超限返回 `429`、`Retry-After` 和 JSON `retryAfterSeconds`。WebSocket 消息预算耗尽返回 `rateLimited` 及重试秒数，使用关闭码 `1013`；登录错误／超时使用 `1008`，消息过大使用 `1009`。关闭和超时会释放名额；单个连接的消息预算不影响其他玩家。失败记录仅在原生进程内保存，重启清空；赛事恢复文件不包含这些短期传输额度。

默认只使用 TCP 直接对端地址，忽略任意 `Forwarded`、`X-Forwarded-For`、`CF-Connecting-IP`。需要反代来源分流时，显式设置 `TrustedProxyAddresses`，并让这些代理**覆盖** `X-Forwarded-For` 为单个真实客户端 IP；多地址链不会被直接采信。限制源站仅由指定代理访问，勿配置信任任意地址。未配置代理信任时，以代理地址聚合额度，部署者应按共享出口人数调整上限。

Cloudflare 使用可选环境变量 `INGRESS_LIMITS`（JSON 字符串），字段名为表中名称的 camelCase 形式，默认额度相同；不使用 `TrustedProxyAddresses`。示例及独立部署步骤见 [Cloudflare README](cloudflare/README.md#入口保护配置)。它仅在平台请求元数据存在且不是 Worker 子请求时采用 `CF-Connecting-IP`；不信任任意 `X-Forwarded-For` 或自定义来源头。来源不可确认时归为 `unknown`，保持同出口的身份隔离；代理、Service Binding 或额外 Worker 链路必须验证实际元数据保留情况，并配合边缘规则，不能通过随意注入 IP 头绕过回退。Cloudflare 的来源头语义见 [官方说明](https://developers.cloudflare.com/fundamentals/reference/http-headers/)。

Cloudflare 将失败窗口写入独立的 `ingress-failures-v1` 存储，将登录截止时间与消息令牌保存在 WebSocket attachment 中，因此 Hibernation／对象重建不会刷新额度。既有连接缺少字段时获得一次默认额度及登录期限；未认证连接由 alarm 清理，正在关闭的连接仍占名额直至平台确认关闭。HTTP 登录请求体限制为 64 KiB，读取超时释放验证名额；消息预算在 JSON 解析前检查。

协议仍为 v2：从 Schema 生成的 `LoginRejected` 与通用错误载荷增加可选 `retryAfterSeconds`，旧客户端仍可读取原有 `code`／`message`。旧客户端可能不按新字段自动退避，重试提示也包含秒数文字；不要将未知错误码视为赛事事件的成功确认。

## 圈完成校验（当前源码）

原生与 Cloudflare 共用同一组校验契约测试。圈事件携带快照提供的可选 `stageId`；阶段不符、已处理或倒退的圈序、分段数量不符、非有限／负时间等明确错误会被拒绝且不计圈。有效圈仍限定 3–21600 秒。首个圈序作为基线；此后的缺号进入待审核，客户端放弃无效圈后允许同圈序重新完成。

回执的可选 `validationStatus` 区分 `verified`、`insufficientEvidence`、`pendingReview`、`rejected`。分段合计按客户端非负分段时长规则核对（容差为 0.05 秒与圈时的 0.1% 中较大者），正常进站耗时仍包含在圈时内。合计矛盾、圈序缺口或充分连续遥测中的进度矛盾生成待审核调查，不自动判作弊或处罚；这些事件仍按原有有效圈规则计圈及更新成绩，管理员通过调查流程裁定。客户端主动报告的无效圈仍可获成功命令回执，但状态为 `rejected`，且不计入有效圈。

进度核对使用事件原始客户端时间窗，不用消息到达间隔。至少 8 个可靠样本、首尾误差不超过 1.5 秒、相邻间隔不超过 2 秒且无进站／暂停／明显进度跳变时才交叉核对；累计进度落在 0.65–1.35 圈之外进入待审核。这些容差只用于筛选待核查证据，不能证明 FH6 中作弊。缺少阶段标识、零时长分段或不连续／进站样本时记为证据不足。在线迟到事件不受断线补圈开关控制；标记为离线补圈的事件仍遵守原有开关和恢复窗口。

协议保持 v2。`stageId`（快照与圈事件）及 `validationStatus`（回执）由现有 Schema 生成三端模型，默认均为空。旧客户端忽略新属性，缺少阶段标识时仍能按既有流程提交，但无法可靠识别跨阶段旧消息；新客户端连接旧服务端时使用原有快照阶段推断，旧服务端忽略事件的新属性，不提供新增校验保证。新服务端保留已接收事件的原回执分类，重试及跨阶段重放不重复计圈／创建调查。原生恢复文件 v1 新增可选参与者字段，旧文件缺少这些字段时使用空证据和既有去重集合；已有持久化提交后才成功回执的约束不变。

## 实时 Delta 的维修区连续性（当前源码）

原生与 Cloudflare 将成绩圈数和用于 Delta 的连续距离分开维护。出站遥测已经确认跨越起终点时，迟到的圈事件只提供距离下界，不再重复增加圈偏移；事件先到时忽略随后到达的过线前旧位置。维修通行只有从主路线末段返回首段才补充距离跨圈，同侧的短暂维修状态变化不增加偏移。起跑线前的首次穿越用于开始第零圈，不计为额外成绩圈。

有效连续遥测恢复后，Delta 在共同赛道距离上继续更新；证据不足时仍可能使用既有冲线时间差或暂不显示，不能把插值视为实际对手遥测。此修复不以遥测补记成绩，缺失圈事件仍需核查客户端记录。

协议保持 v2，没有消息或 Schema 变化；旧客户端可使用服务端修复。原生内部恢复格式仍为 v1，新增的连续性标记缺失时使用默认值，恢复和阶段切换重新建立实时距离历史；Cloudflare 的实时跟踪器也重新建立。身份、成绩、处罚及事件去重的持久化规则不变。两端回归覆盖迟到圈事件、过线前旧位置、短暂维修状态和连续多圈；修复后的真实多人进站仍需实机复测。

## 兼容性

当前正式服务端为 `v0.5.0`：

- LazyForza `1.5.2`：推荐版本，完整支持当前协议模型、服务器收藏与连接测试；
- LazyForza `1.5.1`：完整支持当前协议模型、服务器收藏与连接测试；
- LazyForza `1.5.0`：协议 v2 主要赛事流程兼容，但不具备其版本发布后新增的全部客户端能力；
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

Preview `0.6.0-alpha-1` is recommended with LazyForza `1.5.3-alpha-1`, adding native restart recovery, consistent lap validation and ingress limits. Protocol v2 uses optional additions; older clients can connect but cannot supply new stage and validation evidence. Native success receipts follow durable storage, and restored races await administrator confirmation. Legacy public snapshots lack recovery identities and deduplication records; back up and archive them before starting a new race after upgrade.

LazyForza RaceServer is the independent server for estate racing. Native ASP.NET self-hosting and Cloudflare Durable Objects provide the same protocol, race behavior and browser Race Control.

Version 0.5.0 adds token-protected public live timing, reusable rule templates, portable event projects, multi-role Race Control accounts and warning-only pre-race checks. Native first setup is now restricted to the server terminal, and live Delta resumes correctly after a tire-change pit exit.

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

#### Native restart recovery (current source)

The native server stores internal recovery format version `1` in `data/current-race.json`: room rules, phases and clocks, driver and observer resume identities, lap and sector results, penalty execution state, archived stage results, investigations and collision replays, and lap/pit event deduplication records. This file contains resume tokens and must remain private to the server.

Important changes are saved synchronously; ordinary telemetry is checkpointed about every two seconds. The server writes and flushes a temporary file in the same directory, then atomically replaces the complete state file. Successful lap and pit acknowledgements are sent only after persistence completes, including duplicate submissions. A failed save produces no successful acknowledgement. A missing receipt does not prove that an event was not saved: retry with its original event ID. The JSONL audit log is not the authoritative recovery source.

State loads before listeners and clocks start. An active session returns under a red flag with disconnected drivers. An administrator reviews results, penalties and reconnecting drivers, then restores **full-course green** to resume, or returns to the lobby to start a new session. While awaiting confirmation, clocks and penalty service stay frozen and new lap/pit events remain deferred; saved duplicates may still be acknowledged. Resumption excludes the interval from the last checkpoint to confirmation. Live telemetry continuity is rebuilt and unfinished stationary penalty service restarts; served penalties remain served. Finished sessions and the lobby do not automatically start another race.

Legacy public snapshots lack resume identities and deduplication records, so they cannot be safely converted into resumable sessions. Startup preserves legacy, corrupt or unknown-version files and exits with an error. Back up a legacy snapshot for results review, then move it out of the data directory before starting a new session. Incomplete temporary files never replace the last committed checkpoint. Older server receipts do not retroactively gain recovery guarantees.

The internal format is independent of wire protocol v2 and needs no new Schema fields or client messages. Cloudflare already persists internal state in Durable Object storage before acknowledging events; normal object wakeups retain that workflow and do not enter the native process-restart confirmation gate.

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

### Ingress protection (current source)

Native `RaceServer:Ingress` and Cloudflare `INGRESS_LIMITS` configure failure windows, pending WebSocket capacity and per-connection message/byte token budgets. The JSON Cloudflare variable uses camelCase versions of the native option names; defaults and an example are listed above and in the Cloudflare README. Successful authentication refunds the failure reservation, player identities are isolated within a shared exit, and administrator attempts use a separate channel. A larger source-wide threshold bounds identity rotation; extreme abuse may temporarily delay new logins from that exit but never disconnect existing players.

HTTP throttling returns 429 with `Retry-After` and `retryAfterSeconds`. WebSockets receive a retry error before close code 1013; invalid/timed-out login uses 1008 and oversize messages use 1009. Short-lived native failure counters reset on process restart. Cloudflare persists failure windows separately and retains socket budgets/deadlines in attachments across Hibernation; alarms expire pending logins. Old protocol v2 readers ignore optional retry fields and still receive human-readable hints.

Native ignores proxy headers unless the immediate peer appears in `TrustedProxyAddresses`; configure that proxy to overwrite X-Forwarded-For with exactly one address and firewall direct origin access. Cloudflare uses platform metadata with CF-Connecting-IP and rejects Worker-subrequest identity assumptions; unknown sources fall back to a shared bucket with identity isolation. No arbitrary custom IP header is trusted. Validate real proxy/Worker chains and use edge protections for distributed abuse. Automated tests and local dry-run do not establish public-deployment security or real multi-machine FH6 validation.

### Lap completion validation (current source)

Both authority implementations run shared validation fixtures. Optional snapshot/lap `stageId` isolates stages; stale/reused lap numbers, wrong sector counts and invalid times are rejected without counting. Valid laps remain limited to 3–21600 seconds. The first lap number establishes a baseline; gaps create a review, and an abandoned invalid lap may reuse its number.

Optional acknowledgement `validationStatus` is `verified`, `insufficientEvidence`, `pendingReview` or `rejected`. Sector totals use non-negative client segment durations, including pit time, with tolerance max(0.05 seconds, 0.1% of lap time). Gaps, inconsistent totals and adequately sampled progress contradictions open investigations, without automatic penalties or cheating claims. Pending-review laps still count and update results under existing valid-lap rules. Client-declared invalid laps may receive a successful command acknowledgement with `rejected` status but never count as valid laps.

Progress checks use the original client event window, never arrival spacing. They require at least 8 reliable samples, endpoints within 1.5 seconds, gaps at most 2 seconds and no pit, pause or major progress jump. Accumulated progress outside 0.65–1.35 laps triggers review. Missing stage IDs, zero-duration sectors and incomplete/pit evidence are insufficient evidence. Offline recovery retains its existing opt-in and grace-window rules.

Wire protocol remains v2: all new fields are nullable and generated from the existing Schema for all three targets. Older peers ignore them. New servers accept legacy unstamped events with reduced evidence and cannot guarantee their stage origin; old servers provide no new validation guarantee. Accepted event classifications survive retries, stage transitions and persistence without duplicate laps or investigations. Native recovery v1 adds optional participant fields; older files retain their existing deduplication fallback and empty evidence. Successful acknowledgements still follow persistence.

### Compatibility

RaceServer `0.5.0` is recommended with LazyForza `1.5.2`. The main protocol v2 race flow remains compatible with LazyForza `1.4.2–1.5.1`; features introduced after a client version are unavailable to that older client. Disconnected-lap recovery requires client `1.4.8` or later and must be enabled from Race Control.

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
