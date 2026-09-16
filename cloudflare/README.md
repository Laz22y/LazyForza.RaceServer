# Cloudflare Durable Objects 部署

<p align="center"><a href="#简体中文">简体中文</a> · <a href="#english">English</a></p>

## 简体中文

当前正式版为 `0.6.0`，推荐搭配 LazyForza `1.5.3`。本版新增连接限速，改进圈完成校验、长期房间管理及进站后的实时 Delta；房间状态继续使用 Durable Object 持久保存。协议保持 v2。

开发或修改 Cloudflare 实现前先读仓库根目录 [`AGENTS.md`](../AGENTS.md)。Cloudflare 与原生 ASP.NET 是同一服务端的两套实现，对客户端可见的协议、比赛行为、管理接口和 Web 总控必须保持一致。

这个目录提供与 LazyForza 地产赛事客户端协议 v2 兼容的 Cloudflare Workers + Durable Objects 服务端。协议模型和 Web 静态资源由仓库根目录的单一 Schema 与原生 `wwwroot` 生成，Cloudflare 包保留已提交产物，因此仍可脱离上级目录独立构建和部署。一个 Worker 固定使用一个名为 `main` 的赛事房间，支持 1–12 名车手，并可额外连接最多 12 个只读 OB 席位。OB 不占车手名额，可在比赛进行中加入，只接收赛事数据用于观赛或转播。

正式服务端 `v0.6.0` 推荐搭配 LazyForza `1.5.3`，并与 `1.4.2`–`1.5.2` 的协议 v2 主要比赛流程兼容。断线计圈恢复需要 `1.4.8` 或更高版本，并由总控主动开启。主动退出释放、阶段归属和客户端换胎区计圈修复需要 1.5.3；其他较旧版本的功能范围见仓库根目录 `README.md`。

实现范围：

- 比赛密码、可多用户使用的超管/管理员/裁判总控账号、显示名、主题色、可选车队和断线恢复；
- 由总控选择开启的 30 秒断线计圈恢复，补交事件支持确认和去重；
- 1–3 节练习赛、每节默认 60 分钟或由总控逐节设置、独立圈速排名和最后一圈收尾；
- 可保存、覆盖、应用和删除的赛事规则模板，赛事名称、赛道与车队资料保持独立；
- 可复用赛事项目及 `.lfzevent` 导入导出，项目包含房间规则、赛程、车队、赛道、Logo、阶段赛果和赛事记录；
- 大厅、兼容单节的 1–3 节排位、默认或自定义淘汰、每节最后飞驰圈、完整排位顺位、出场圈和暖胎圈；
- 五盏红灯随机熄灭发车、抢跑自动加罚、正赛、红旗暂停和自动方格旗；
- 发车前检查车手连接、准备、遥测、维修区、赛道与规则状态；警告可由总控确认后忽略并强制发车；
- 全场最快圈、排名、排位/正赛自动黄旗、人工分区/全场黄旗、自动蓝旗、处罚、DNF/DSQ、维修停留进度；
- 练习、排位与正赛均可生成碰撞待审调查；
- 接收客户端路线收益切弯证据，并保持正赛首圈、换位和进出维修区后的实时秒差连续；
- WebSocket Hibernation，空闲连接不要求 Worker 一直驻留；
- SQLite 后端 Durable Object 保存关键赛事状态；
- 复用 .NET 自托管版的 Web 总控静态页面；
- 独立只读令牌保护的公开实时计时页，适配手机、Pad 和直播浏览器源；
- 与自托管版一致的判罚/调查区、赛后加时结算及处罚修改和取消接口。

客户端遥测默认 10 Hz，但房间快照广播最多 10 Hz。服务端不采信遥测消息中的累计圈数，只有唯一且有效的 `lapCompleted` 事件能把服务端权威圈数增加一圈。维修区只记录停留条件和次数，不能证明游戏已经更换轮胎或重置车损。

## 长期房间与赛事管理

总控分为「比赛现场」「赛事项目」「规则与赛程」「赛果与记录」「服务器」。顶部始终显示当前项目和比赛阶段，项目支持搜索及状态筛选。

- **一场赛事、一个项目**：练习、排位和正赛属于同一场赛事。新建项目只保存当前配置、赛程与素材，不带入上一场成绩。编辑资料仅修改名称、主办方等信息；在「规则与赛程」保存时更新当前房间和活动项目。
- **模板是规则副本**：应用模板后可以继续修改，保存房间不会反向修改模板。房间赛程也会保存，重新打开网页不会回到默认值。比赛进行中须先返回大厅才能改规则；正赛完成后先准备新一场。
- **准备新一场**：在大厅或比赛结束后操作，先保留上一项目的成绩、处罚和记录，再清理实时成绩、处罚与离线占位。在线车手保留身份，但需要重新准备。启用草稿项目时同时载入其规则、赛程、赛道和 Logo。已完成项目需要复制后用于下一场；导入带赛果的项目视为已完成。
- **退出与掉线分开**：客户端主动退出房间或关闭模块会发送 `leave`，服务端持久释放身份后才回复 `left` 并正常关闭连接（1000）。车手席位、名称和 OB 名额随之释放，已产生的成绩及处罚留在阶段赛果中。暂时掉线保留恢复身份；大厅中掉线占位在五分钟后释放，比赛进行中保留到主动退出、总控移除或准备新一场。旧客户端没有主动退出消息时，可等待大厅清理或由总控移除。

协议仍为 v2。`leave` / `left` 与快照、阶段赛果、审计事件上的可选 `eventId` 均从现有 Schema 生成三端模型。新客户端只有收到 `left` 才删除已保存的恢复令牌；连接旧服务端时，等待最多一秒后断开并保留令牌，不承诺立即释放。旧客户端忽略可选字段，已有阶段及事件去重规则继续生效。`eventId` 是 LazyForza 服务端的赛事归属标识，不是 FH6 官方赛事 ID。

原生内部恢复格式仍为 v1，新增可选赛事归属；项目交换包也保持 v1 并增加可选归属字段。缺少标识的旧记录归入旧赛事组，保留原记录，不根据名称猜测并拆分历史比赛。旧版已释放的车手记录会先保存为阶段成绩，再释放运行缓冲。只含公开快照、更早期且缺少恢复身份的文件仍按上文规则拒绝续赛。

原生切换通过版本 1 的 `pending-event.json` 保存切换意图，再更新赛事、配置、素材及项目。成功响应表示这些保存已完成；中途失败保留恢复记录，阻止其他管理修改，允许重试切换。启动时在对外监听前重放未完成的切换，不会重复创建一场赛事。Cloudflare 将同一组变更放入 Durable Object 存储事务，提交后才替换内存状态和响应。升级前备份完整数据目录／导出项目；如需退回旧程序，恢复升级前的数据备份，不混用新旧运行状态。

总控保留近期 24 个阶段结果；长期办赛应使用项目保存、导出和归档记录。项目数量及审计记录仍受现有容量限制。原生重启后的管理员续赛确认规则不变，Cloudflare 对象正常唤醒沿用既有行为。

## 入口保护配置

在 Cloudflare Dashboard 设置普通变量 `INGRESS_LIMITS`，或编辑 `wrangler.jsonc` 的同名变量。值为 JSON 字符串，缺省或 `{}` 使用以下默认配置；无需添加 Secret 或修改协议版本：

```json
{
  "loginFailureLimit": 5,
  "sourceLoginFailureLimit": 120,
  "loginFailureWindowSeconds": 60,
  "maximumConcurrentLogins": 32,
  "maximumFailureBuckets": 4096,
  "maximumUnauthenticatedConnections": 64,
  "maximumUnauthenticatedPerSource": 32,
  "loginTimeoutSeconds": 12,
  "messagesPerSecond": 60,
  "messageBurst": 120,
  "bytesPerSecond": 131072,
  "byteBurst": 262144
}
```

例如 `wrangler.jsonc` 中可设置 `"INGRESS_LIMITS": "{\"loginFailureLimit\":8}"`，其余值保持默认。参数必须为正整数；来源失败额度不得小于单身份额度，单来源连接上限不得超过全局上限。字段含义与原生版相同，见根 README 的入口保护表。

成功登录不消耗失败额度；玩家名字／恢复令牌摘要与来源共同隔离失败窗口，总控通道独立。不要把来源总额度设得过低，否则同 NAT 的集中重连可能遭遇短时 429。已登录的 12 名车手和 12 个 OB 不占未登录连接额度，每条连接独享消息预算。超限 HTTP 返回 `Retry-After`，WS 返回重试秒数后以 1013 关闭；无效／超时登录以 1008 关闭。旧客户端仍能读取文字提示，但可能不会自动遵守重试字段。

仅当请求保留 Cloudflare 平台元数据且不是 Worker 子请求时使用平台 `CF-Connecting-IP`；其余来源回退为 `unknown`。不接受任意 X-Forwarded-For、自定义 IP 头或来自额外 Worker 的来源自报。使用额外代理／Worker／Service Binding 时应核对来源元数据，必要时在边缘做额外保护；不得用伪造头解决共享桶限速。参见 [Cloudflare 来源头说明](https://developers.cloudflare.com/fundamentals/reference/http-headers/)。

失败记录保存到 `ingress-failures-v1`，额度窗口不会因 DO 重建而清空；连接预算和登录期限存于 attachment，alarm 负责超时回收，关闭过程中的未登录连接仍占名额。已部署旧连接缺失这些字段时获得一次初始额度。此存储独立于赛事数据，不需要删除房间或重建数据库。HTTP 请求体最多 64 KiB，慢请求超时会归还验证名额。Vitest 使用确定性的 DO/Socket 模拟，实际 alarm、Hibernation 和公网来源链仍需部署环境验证。

## 网页一键部署

点击下面的按钮，登录自己的 Cloudflare 账号并确认创建 Worker 与 Durable Object：

[![Deploy to Cloudflare](https://deploy.workers.cloudflare.com/button)](https://deploy.workers.cloudflare.com/?url=https://github.com/Laz22y/LazyForza.RaceServer/tree/main/cloudflare)

Cloudflare 会把这个公开模板复制到你的 GitHub 或 GitLab 账号，自动创建 Durable Object 绑定并配置后续提交的构建部署。`cloudflare` 子目录已包含 Worker、依赖锁文件和控制面板静态资源，不依赖仓库上级目录。

部署完成后直接打开 Cloudflare 分配的域名。网页第一次打开只需要设置房间密码、初始超管密码和房间基础规则，不要求同时设置管理员、裁判或赛道文件信息。超管之后可按需创建多个独立账号；管理员可管理赛事但不能管理总控账号，裁判仅处理判罚与调查。房间密码没有最少位数限制，总控账号密码仍需 8–128 个字符，密码只以加盐摘要保存在 Durable Object 中。初始化完成后，到总控页面上传 LazyForza 导出的 `.lfzestate`，服务端会自动识别并填写赛道名称、标识、地图修订和稳定特征值。公开计时链接在总控的“公开实时计时”区生成，令牌明文只显示一次，轮换或停用后旧链接立即失效。完成设置后，把域名与房间密码发给车手，总控密码只交给对应工作人员。

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
npm run check:generated
npm run check
npm test
npm run dry-run
npm run dev
```

本地测试和 dry-run 不等于中国大陆网络直连或真实 FH6 多机联机验证。实际使用前仍应从参赛车手所在网络测试 HTTPS、WebSocket 握手、10 Hz 状态更新和短时断线恢复。

## English

Stable release `0.6.0` is recommended with LazyForza `1.5.3`. It adds native restart recovery and ingress limits, with improved lap validation, persistent-room management and live Delta after pit stops. Protocol v2 remains in use.

Read the repository-level [`AGENTS.md`](../AGENTS.md) before changing the Cloudflare implementation. Cloudflare Durable Objects and native ASP.NET are equal RaceServer targets and must keep the same client protocol, race behavior, management API and Race Control features.

RaceServer `0.6.0` is recommended with LazyForza `1.5.3` and remains compatible with the main protocol v2 race flow in LazyForza `1.4.2–1.5.2`. Disconnected-lap recovery requires client `1.4.8` or later and must be explicitly enabled from Race Control. Explicit departure, stage ownership and the client-side service-box lap fix require `1.5.3`.

Protocol models and browser assets are generated from the repository-level schema and native `wwwroot`. Their committed outputs keep this Cloudflare package independently buildable and deployable. The Worker uses one Durable Object race room named `main`, with 1–12 drivers and up to 12 read-only observers. It supports:

- room passwords, named super-admin, administrator and steward accounts for multiple Race Control users, display names, colors, teams and reconnect recovery;
- reusable race-rule templates that keep event, track and team details separate;
- reusable event projects with `.lfzevent` import and export for room rules, schedules, teams, track, logo, session results and race logs;
- one to three practice and qualifying sessions, out laps, formation laps, five red lights, races and red flags;
- standings, live gaps, flags, penalties, DNF/DSQ, collision investigations, shortcut evidence and pit progress;
- optional 30-second disconnected-lap recovery with acknowledged, deduplicated lap events;
- hosted `.lfzestate` track packages, organizer logos, WebSocket Hibernation and persistent Durable Object state;
- the same Chinese and English browser Race Control used by the native server;
- public live timing protected by an independent read-only token, with phone, tablet and transparent broadcast layouts.

Client telemetry defaults to 10 Hz and room snapshots are broadcast at no more than 10 Hz. Only a unique valid `lapCompleted` event advances the authoritative lap count. Pit state records location and dwell conditions; it cannot prove that the game changed tires or repaired damage.

### Persistent rooms and event management

Race Control now separates Live race, Events, Rules & schedule, Results & log, and Server. One project owns one event across practice, qualifying and race. New projects copy configuration and assets without old results; metadata edits preserve rules. Rule templates are reusable copies. Room schedules persist, and saving rules updates the active project. Return to the lobby to change rules; after a completed race, prepare the next event first.

Preparing another event retains archived results and penalties, releases offline seats and resets live competition state; connected drivers keep their identity and must ready again. Completed projects are copied for reuse. Imported projects with results are completed records. Explicit client departure uses `leave` / `left`: the server persists identity release before acknowledging and closing normally (1000). Temporary disconnections retain recovery identity; lobby reservations expire after five minutes. Older clients need lobby expiry or administrator removal.

Protocol v2 remains, with Schema-generated leave messages and optional `eventId` ownership fields. New clients clear saved resume tokens only after `left`; old servers time out after one second and the token remains saved. Native recovery and project export formats stay at v1 with optional ownership. Legacy records without an event ID remain one legacy group; old released driver entries are compacted into results before removing runtime buffers.

Native event switches use a versioned `pending-event.json` intent, replayed before listening after interruption. Success means event state, settings, assets and project ownership are saved. Cloudflare commits the equivalent change in one Durable Object storage transaction. Back up all data before upgrading; restore the pre-upgrade backup when reverting to older binaries. Recent room history retains 24 stages; save/export projects for long-term records. Existing native restart confirmation and Cloudflare wake-up behavior remain unchanged.

### One-click deployment

[![Deploy to Cloudflare](https://deploy.workers.cloudflare.com/button)](https://deploy.workers.cloudflare.com/?url=https://github.com/Laz22y/LazyForza.RaceServer/tree/main/cloudflare)

Cloudflare copies this public template to your GitHub or GitLab account, creates the Durable Object binding and configures deployment from later commits. After deployment, open the Worker domain and create the initial super-admin account; other roles are optional and can be added later. Super admins have full access, administrators manage the race but not Race Control accounts, and stewards handle penalties and investigations only. Then upload the matching `.lfzestate` package from Race Control. Public viewer and transparent broadcast links are generated in the Public Live Timing panel; rotating or disabling the separate read-only token invalidates every previous link.

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
npm run check:generated
npm run check
npm test
npm run dry-run
npm run dev
```

Local tests and dry-runs do not prove public-network reachability or real FH6 multi-PC behavior. Test HTTPS, the WebSocket handshake, 10 Hz state updates and short reconnect recovery from the drivers' actual networks before an event.

## 圈校验兼容

原生与 Cloudflare 的圈完成校验和回执分类保持一致，规则及旧协议限制见上级 [README](../README.md#圈完成校验)。可选 `stageId` 与 `validationStatus` 保持协议 v2；旧客户端缺少阶段证据仍可提交。待审核圈创建调查并沿用现有计圈规则，不自动处罚。Durable Object 保存圈序和回执分类，重建后重复事件不再次计圈或创建调查。共享测试数据位于 `tests/fixtures/lap-validation-cases.json`，随独立 Cloudflare 包保留。

Cloudflare shares native lap validation and optional protocol v2 fields. Review findings do not automatically penalize drivers; accepted laps retain existing scoring rules. Durable Object state preserves sequence and receipt classification across reconstruction. Shared fixtures are included in this standalone package; see the parent README for compatibility and evidence limits.

### Ingress configuration

`INGRESS_LIMITS` is an optional JSON string in Dashboard variables or wrangler.jsonc. The configuration example above lists all defaults. Failed logins are partitioned by source and player identity; successful verification refunds its reservation and administrators use a separate channel. A higher source threshold bounds identity rotation. Existing authenticated drivers/observers do not consume pending-login slots and each socket has an independent message/byte budget.

HTTP throttling returns 429 and Retry-After; WebSockets include retry seconds before closing with 1013. Invalid or expired logins close with 1008. Failure windows persist in a separate DO storage key, socket budgets/deadlines persist in attachments, and alarms expire pending connections across Hibernation. Only platform source metadata is used; Worker subrequests and missing metadata fall back to unknown, never arbitrary proxy headers. Validate your deployed proxy/Worker chain and use edge protection for distributed abuse; local simulated tests do not replace that validation.

### Delta 距离跟踪

与原生端共用相同规则：有效主路线和维修通路进度持续进入有界距离时间线，进站与换胎标记不控制采样。迟到圈事件只提供距离下界，接收时刻不作为过线时间；遥测不补记成绩。阶段切换和对象恢复后重新建立实时历史，已保存的成绩、处罚与去重记录继续保留。协议保持 v2，详见[距离跟踪说明](../README.md#实时-delta-的距离跟踪)。
