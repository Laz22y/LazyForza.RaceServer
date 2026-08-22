# LazyForza RaceServer

LazyForza 地产赛事的独立服务端。支持原生 ASP.NET 自托管和 Cloudflare Durable Objects，两套实现保持同一协议与 Web 总控功能。

[客户端下载](https://github.com/Laz22y/LazyForza/releases/latest) · [完整文档](https://laz22y.github.io/LazyForza/docs/#race-server) · [服务端 Releases](https://github.com/Laz22y/LazyForza.RaceServer/releases/latest)

## 提供什么

- 1–12 名车手，可单人发车；额外支持最多 12 个只读 OB 席位；
- 1–3 节练习与排位、正赛、出场圈、暖胎圈、五盏红灯和方格旗；
- 车队、维修区、旗语、处罚、全阶段碰撞调查、路线收益切弯证据、DNF/DSQ 和可选断线计圈恢复；
- 赛道文件与主办方 Logo 托管；
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

当前正式服务端为 `v0.4.2`：

- LazyForza `1.4.9`：完整支持路线收益切弯证据、收紧后的碰撞识别和维修区轨迹保护；
- LazyForza `1.4.8`：完整支持弱网状态提示与可选断线计圈恢复；
- LazyForza `1.4.7`：支持增强碰撞证据与其发布时的全部赛事交互；
- LazyForza `1.4.2–1.4.6`：协议 v2 主要赛事流程兼容，但不具备其版本发布后新增的全部客户端能力；
- LazyForza `1.4.1` 及更早版本没有当前地产赛事客户端，不列入支持范围。

## 本地开发

需要 .NET SDK 9。Cloudflare 端另需 Node.js 20+：

```powershell
dotnet restore LazyForza.RaceServer.sln
dotnet build LazyForza.RaceServer.sln -c Release --no-restore
dotnet test LazyForza.RaceServer.sln -c Release --no-build --no-restore

cd cloudflare
npm ci
npm run check
npm test
```

运行原生端：

```powershell
dotnet run --project src/LazyForza.RaceServer.Web/LazyForza.RaceServer.Web.csproj
```

任何协议、总控接口或 Web 功能变更都必须同步修改原生端与 Cloudflare 端，并补齐双端测试。

## 数据与验证边界

- 原生端将设置、赛事快照和审计日志保存在 `data`；Cloudflare 端使用 Durable Object 存储；
- 服务端不判断游戏是否真的更换轮胎，也不伪造 FH6 未提供的数据；
- 4–12 人和 OB 已有确定性自动测试覆盖；真实 FH6 多机联机结论必须单独记录，不能用模拟器或自动测试替代。

## License

[MIT](LICENSE)。LazyForza RaceServer 是非官方社区项目，与 Microsoft、Xbox 或 Playground Games 无隶属关系。
