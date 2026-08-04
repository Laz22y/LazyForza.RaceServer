# Cloudflare Durable Objects 部署

这个目录提供与 LazyForza 地产赛事客户端协议 v2 兼容的 Cloudflare Workers + Durable Objects 服务端。一个 Worker 固定使用一个名为 `main` 的赛事房间，支持 1–12 名车手。

实现范围：

- 比赛密码、总控密码、显示名、主题色、可选车队和断线恢复；
- 大厅、排位赛最后飞驰圈、出场圈、暖胎圈、五盏红灯随机熄灭发车、抢跑自动加罚、正赛、红旗暂停和自动方格旗；
- 全场最快圈、排名、排位/正赛自动黄旗、人工分区/全场黄旗、自动蓝旗、处罚、DNF/DSQ、维修停留进度；
- WebSocket Hibernation，空闲连接不要求 Worker 一直驻留；
- SQLite 后端 Durable Object 保存关键赛事状态；
- 复用 .NET 自托管版的 Web 总控静态页面。

客户端遥测默认 10 Hz，但房间快照广播最多 10 Hz。服务端不采信遥测消息中的累计圈数，只有唯一且有效的 `lapCompleted` 事件能把服务端权威圈数增加一圈。维修区只记录停留条件和次数，不能证明游戏已经更换轮胎或重置车损。

## 网页一键部署

点击下面的按钮，登录自己的 Cloudflare 账号并确认创建 Worker 与 Durable Object：

[![Deploy to Cloudflare](https://deploy.workers.cloudflare.com/button)](https://deploy.workers.cloudflare.com/?url=https://github.com/Laz22y/LazyForza.RaceServer/tree/main/cloudflare)

Cloudflare 会把这个公开模板复制到你的 GitHub 或 GitLab 账号，自动创建 Durable Object 绑定并配置后续提交的构建部署。`cloudflare` 子目录已包含 Worker、依赖锁文件和控制面板静态资源，不依赖仓库上级目录。

部署完成后直接打开 Cloudflare 分配的域名。网页第一次打开只需要设置密码和房间基础规则，不要求填写赛道文件信息；房间密码没有最少位数限制，总控密码仍需 8–128 个字符，密码只以加盐摘要保存在 Durable Object 中。初始化完成后，再到总控页面填写 LazyForza 显示的赛道名称、标识和 SHA-256。完成设置后，把域名与房间密码发给车手，总控密码只由赛事管理员保留。

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
- LazyForza 的“赛事信息”按钮以及 `.lfzestate` 导出完成窗口都会显示可复制的赛道名称、标识和数据 SHA-256；
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
npm install
npm run check
npm test
npm run dry-run
npm run dev
```

本地测试和 dry-run 不等于中国大陆网络直连或真实 FH6 多机联机验证。实际使用前仍应从参赛车手所在网络测试 HTTPS、WebSocket 握手、10 Hz 状态更新和短时断线恢复。
