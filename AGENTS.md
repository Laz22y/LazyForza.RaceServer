# LazyForza RaceServer 开发入口

本文面向第一次接触仓库的 Coding Agent。RaceServer 是 LazyForza 地产赛事的权威服务端，同时维护原生 ASP.NET 和 Cloudflare Durable Objects 两套实现；二者不是主次或演示关系。

## 事实来源与阅读顺序

发生冲突时，按代码和配置 → 测试 → CI/脚本 → 本文与 README → 历史说明的顺序判断。

收到任务后按需阅读：

1. 本文和任务涉及的实现；
2. `.NET` 行为先看 `tests/LazyForza.RaceServer.Tests`，Cloudflare 行为先看 `cloudflare/tests`；
3. 协议任务同时打开 `src/LazyForza.RaceServer.Protocol/RaceProtocolModels.cs`、客户端 `../LazyForza\src\LazyForza.Modules.EstateRace\EstateRaceWireProtocol.cs` 和 `cloudflare/src/protocol.ts`；
4. 比赛规则同时比较 `RaceCoordinator.cs` 与 `cloudflare/src/race-core.ts`；
5. Web 总控任务同时比较原生 `wwwroot` 与 `cloudflare/public`，以及两端路由；
6. 涉及真实 FH6、弱网或多机结论时阅读客户端 [`VALIDATION_WITH_FH6.md`](../LazyForza/VALIDATION_WITH_FH6.md)。

不要把只在一个实现通过的功能描述为 RaceServer 已完成。

## 系统概览

仓库使用 .NET SDK 9.0.316、ASP.NET Core、MSTest、TypeScript、Vitest、Wrangler 和 Cloudflare Durable Objects。原生端负责 HTTP/WebSocket、静态总控、赛事状态和文件持久化；Cloudflare 端提供相同的外部协议、管理能力和 Web 页面，并把房间状态存入 Durable Object。

客户端从 FH6 官方 UDP 推导本车遥测后上传。服务端不读取游戏，不从连续遥测中的累计圈数直接增加权威圈数；唯一有效的 `lapCompleted` 事件、房间规则和管理员操作决定排名、圈数、旗语、处罚与阶段结果。

## 项目边界

| 项目/目录 | 当前职责 | 不应放入 |
| --- | --- | --- |
| `LazyForza.RaceServer.Protocol` | 协议 v2 常量、消息、DTO、枚举、快照和管理命令 | 房间状态、ASP.NET 路由 |
| `LazyForza.RaceServer.Core` | 原生房间权威状态机、排名、阶段、维修、旗语、处罚、调查和结果归档 | HTTP/WebSocket 细节、HTML |
| `LazyForza.RaceServer.Web` | ASP.NET 路由、认证会话、WebSocket 注册/广播、托管文件、原生持久化和静态总控 | Cloudflare 平台 API |
| `LazyForza.RaceServer.Tests` | 协议/Core/Web 边界、持久化和托管文件回归 | Cloudflare 行为替代证明 |
| `cloudflare/src/protocol.ts` | TypeScript 协议副本 | 只在 TS 存在的新语义 |
| `cloudflare/src/race-core.ts` | Durable Object 端赛事状态机和序列化 | DOM 与静态页面逻辑 |
| `cloudflare/src/index.ts` | Worker/DO 路由、认证、WebSocket Hibernation、存储与 alarm 接线 | 与原生端不兼容的接口 |
| `cloudflare/public` | Cloudflare 总控静态资源副本 | 独有页面或文案 |
| `cloudflare/tests` | TypeScript 协议、核心、密码和赛道包回归 | 原生实现替代证明 |
| `scripts` | 部署、开发预览与正式发行 | 运行时用户数据 |

`Protocol` 不依赖 `Core`/`Web`；`Core` 只依赖 `Protocol`；平台接线位于 Web 或 Cloudflare `index.ts`。不要在路由层重新实现比赛状态机。

## 三套契约与两套行为

协议没有共享代码生成器，以下副本必须人工保持兼容：

- 客户端：`../LazyForza\src\LazyForza.Modules.EstateRace\EstateRaceWireProtocol.cs`、`EstateRaceModels.cs`；
- 原生服务端：`src/LazyForza.RaceServer.Protocol/RaceProtocolModels.cs`；
- Cloudflare：`cloudflare/src/protocol.ts`。

当前协议版本为 2，JSON 使用 camelCase 属性和字符串枚举，消息上限 64 KiB；参赛车手和 OB 上限分别为 12。改变 message type、DTO 字段、枚举、默认值、可空性、错误码或序列化名称时：

1. 判断旧客户端与旧服务端如何读取新消息，优先用可选字段保持协议 v2 兼容；
2. 同步三份模型和相应序列化；
3. 同步 `RaceCoordinator.cs` 与 `cloudflare/src/race-core.ts`；
4. 同步 `RaceWebSocketHandler.cs`/`Program.cs` 与 `cloudflare/src/index.ts`；
5. 补客户端网络流、RaceServer MSTest 和 Cloudflare Vitest；
6. 更新 README 的版本兼容说明，但不要把未发行代码写成正式版本。

连续遥测采用 latest-wins，允许丢弃过时位置；圈完成和维修完成是带事件 ID、确认与去重的可靠命令。不要把权威事件塞回遥测快照。恢复令牌应恢复同一参与者状态，不能创建重复车手。

## 原生与 Cloudflare 对等规则

常见修改的同步范围：

| 修改类型 | 原生端 | Cloudflare 端 | 测试 |
| --- | --- | --- | --- |
| 比赛阶段、排名、Delta、进站、旗语、处罚、调查、赛果 | `RaceCoordinator.cs` 和 Protocol | `race-core.ts`、必要时 `protocol.ts` | `RaceCoordinatorTests.cs` + `race-core.test.ts` |
| 登录、恢复、WebSocket 消息 | `RaceWebSocketHandler.cs`、`RaceWebSocketRegistry.cs`、`Program.cs` | `index.ts`、`protocol.ts` | registry/协调器测试 + Vitest |
| 管理 API 或设置 | `Program.cs`、配置/认证存储 | `index.ts`、DO 存储 | 两端 API/核心行为测试 |
| 赛道文件或 Logo | Hosted store、路由、上限/校验 | `track-package.ts`、`index.ts`、DO 存储 | 对应 .NET 与 TS 文件测试 |
| Web 总控布局、文案、交互 | `src/...Web/wwwroot/*` | `cloudflare/public/*` | 文件一致性检查 + 两端浏览器人工检查 |
| 持久化模型 | `RaceStatePersistence`、JSON 文件兼容 | `StoredRaceState`、DO key/SQLite storage | 恢复/序列化回归 |

原生 `wwwroot` 和 `cloudflare/public` 当前是实体副本。相同文件必须保持逐字节一致；新增资源还要检查开发/发行脚本的 Cloudflare 打包白名单，不能只确认源码目录中存在。

截至 2026-08-24，`scripts/Publish-Development.ps1` 的 Cloudflare 白名单没有列出已经被 `index.html` 引用的 `public/lazyforza-logo.png`。这是本次文档审计发现的打包缺口，未在文档任务中修改生产脚本；验证开发/发行包时必须单独检查该资源，修复时补包内容回归。

Cloudflare 平台允许不同的存储和 WebSocket 生命周期实现，但对客户端可见的协议、赛事结果、管理动作和错误语义必须与原生端一致。平台差异应限制在 `index.ts`/持久化接线，不应扩散到比赛规则。

## 状态与兼容不变量

- 服务端不读取 FH6，也不能证明游戏内真的换胎、维修或重置车损。维修状态只表示 LazyForza 的位置和停留规则。
- `lapCompleted` 的事件 ID 保证重连补交幂等；服务端不采信遥测消息中的累计圈数。
- 阶段结束后的结果需要归档，返回大厅不能使练习、排位或正赛结果消失。
- 密码只保存加盐摘要。房间密码与总控密码职责不同；总控密码必须满足当前配置校验且不能与房间密码相同。
- 弱网或失联客户端不得阻塞其他连接、广播周期或赛事时钟。慢 socket 的发送、替换与清理必须局部化。
- 原生端的 `data/current-race.json` 使用原子替换，`race-audit.jsonl` 为追加审计，`server-settings.json` 保存初始化设置。修改结构时保留旧文件可读取性和用户数据。
- Cloudflare 使用 Durable Object 存储、alarm 和 WebSocket Hibernation。修改存储 key、迁移或序列化模型时必须覆盖已有房间恢复。
- `.lfzestate` 托管包必须校验清单和 SHA-256，大小限制与客户端导入校验保持一致；不要绕过验证直接信任上传元数据。
- 4–12 人确定性测试不等于对应数量真实 FH6 联机验证。

## 构建与验证

在仓库根目录使用 PowerShell 7。完整本地检查与 CI 一致：

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

Cloudflare 本地运行：

```powershell
cd cloudflare
npm run dev
```

验证层级：

| 改动 | 最低自动验证 | 仍需人工/外部验证 |
| --- | --- | --- |
| Protocol/Core 纯逻辑 | 对应 MSTest + Vitest；共享行为两套都跑 | 通常无 |
| ASP.NET 路由/持久化 | .NET build/test，本地启动并检查 API/恢复 | 反代、TLS、防火墙、真实长期数据 |
| Cloudflare 路由/存储 | `check`、Vitest、`dry-run` | 实际 Worker 部署、alarm/Hibernation、公网网络 |
| Web 总控 | 双目录一致性、两端均能加载 | 电脑宽屏、Pad 触控、浏览器交互和导出效果 |
| 客户端协议或赛事网络 | 服务端双套测试 + 客户端相关集成测试 | 真实多机、丢包/延迟/重连和完整比赛 |

测试通过只能证明其覆盖的确定性行为。本地 WebSocket、Wrangler dry-run、Simulator 和多进程测试不能替代真实 FH6、多台电脑、参赛车手所在网络或 Cloudflare 实际部署。把实机结论记录在客户端 `VALIDATION_WITH_FH6.md`，不要在普通测试报告中扩大结论。

## Web 总控检查

修改静态资源后至少确认：

```powershell
$native = 'src/LazyForza.RaceServer.Web/wwwroot'
$cloudflare = 'cloudflare/public'
Get-ChildItem $native -File | ForEach-Object {
    $peer = Join-Path $cloudflare $_.Name
    if (!(Test-Path $peer) -or (Get-FileHash $_.FullName).Hash -ne (Get-FileHash $peer).Hash) {
        Write-Error "Web asset differs: $($_.Name)"
    }
}
```

再分别从原生端和 Wrangler 打开页面，检查初始化、登录、房间设置、比赛控制、处罚/调查、事件、车手/OB、阶段赛果和 Pad 窄宽度。截图只能证明视觉状态，不证明按钮已接到正确 API。

## 数据、Git 与发行

- 不提交或打包 `data`、密码、审计日志、赛道文件、Logo、运行时配置和用户备份。
- 保留工作树中不属于当前任务的修改；不要重置、覆盖或大范围格式化。
- 普通开发不发行、不推送。开发预览默认只生成 `win-x64`；正式发行才生成所有受支持平台和 Cloudflare 源码包。
- 正式发行只在用户明确要求时执行。版本号独立于客户端，Release 说明列明适用的全部 LazyForza 客户端版本。
- 发布或兼容性变化时同步根 README、`cloudflare/README.md`、客户端 README/完整文档和官网。

## 完成检查

交付前确认：修改没有只落在一套服务端；协议三份模型兼容；Web 两份资源一致；持久化能读取旧状态；弱连接不会反压其他连接；两套测试均覆盖根因；执行了 .NET Release 检查和 Cloudflare check/test/dry-run 中与风险匹配的部分；未验证的公网或实机行为已明确说明。
