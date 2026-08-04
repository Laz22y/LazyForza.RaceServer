# LazyForza Race Server

LazyForza 地产赛事的自托管服务端。单个赛事房间支持 1–12 名车手，提供：

- WebSocket 客户端连接、比赛密码、断线恢复令牌；
- 排位赛最后飞驰圈、出场圈、暖胎圈、五盏红灯随机熄灭发车、抢跑自动加罚、正赛和自动方格旗；
- 排位赛和正赛自动黄旗、人工分区/全场黄旗、红旗、自动蓝旗、判罚、退赛和取消资格；
- 车手显示名、主题色，以及可由房主关闭的轻量车队展示；
- 浏览器赛事总控、实时排名与 JSON 审计日志；
- Windows、Linux、macOS 的 .NET 9 自托管运行方式。
- 可由用户部署到自己 Cloudflare 账号的 Durable Objects 版本。

服务端不读取游戏，也不判断轮胎是否真的更换。车手位置、圈速、维修区状态和抓地趋势由各自的 LazyForza 客户端通过官方 FH6 UDP 推导并上传。

## 本地运行

需要 .NET 9 SDK：

```powershell
dotnet run --project src/LazyForza.RaceServer.Web/LazyForza.RaceServer.Web.csproj
```

默认地址是 `http://0.0.0.0:24876`，总控页面是 `/`，客户端 WebSocket 地址是 `/ws`。第一次打开总控页面时，网页会要求设置房间密码、总控密码、圈数和赛道分段数。房间密码没有最少位数限制，总控密码仍需 8–128 个字符，二者不能相同；服务端只把加盐密码摘要写入 `data/server-settings.json`。

下载开发包后直接启动对应程序，再用浏览器完成首次设置：

```powershell
# Windows
./LazyForza.RaceServer.Web.exe

# Linux / macOS（ZIP 从 Windows 生成，首次解压后需要补一次执行权限）
chmod +x ./LazyForza.RaceServer.Web
./LazyForza.RaceServer.Web
```

通过公网域名提供服务时，应使用 Caddy、Nginx 或其他反向代理终止 TLS，让客户端连接 `wss://`。不要直接把明文 `ws://` 暴露到互联网。

## 发布可运行包

使用 PowerShell 7：

```powershell
./scripts/Publish-Development.ps1
```

脚本默认生成 `win-x64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64` 五种独立运行包，并写入 `artifacts/development`。可用 `-Runtime win-x64` 只生成一个平台。

## Cloudflare Durable Objects

希望避免长期维护 VPS 时，可使用 `cloudflare` 目录中的 Workers + Durable Objects 实现：

[![Deploy to Cloudflare](https://deploy.workers.cloudflare.com/button)](https://deploy.workers.cloudflare.com/?url=https://github.com/Laz22y/LazyForza.RaceServer/tree/main/cloudflare)

点击按钮后登录自己的 Cloudflare 账号，确认 Worker 与 Durable Object 配置即可。部署模板完全位于 `cloudflare` 子目录，不依赖仓库其他目录；没有预先写入 Secret 时，第一次打开部署后的网页会进入首次设置流程。也可以运行 `scripts/Deploy-Cloudflare.ps1` 从本机部署。完整说明见 `cloudflare/README.md`。

## 配置要点

- `MaximumParticipants` 只能为 1–12；
- `SectorCount` 必须与参赛车手导入的地产赛道分段数一致；
- 总控页面可填写导出 `.lfzestate` 时显示的赛道名称、赛道标识和数据 SHA-256；客户端加入时自动选择并核对本地赛道；
- `data/current-race.json` 保存重要赛事快照；
- `data/audit-*.jsonl` 保存总控操作与重要事件；
- 未完成首次设置时，服务启动日志会明确提示；此时不要把地址公开给无关人员。

## 验证边界

自动测试覆盖 1 人发车、2–12 人容量、登录资料、赛道身份、排位最后飞驰圈、五盏红灯、抢跑、正赛排序、维修区状态、黄/红/蓝旗、处罚和断线恢复。4–12 人属于确定性测试覆盖；只有完成真实 FH6 多机联机后，才可记录为实机验证。
