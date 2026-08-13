# LazyForza Race Server

LazyForza 地产赛事的自托管服务端。单个赛事房间支持 1–12 名车手，并可额外连接最多 12 个只读 OB 席位，提供：

- WebSocket 客户端连接、比赛密码、断线恢复令牌；
- 支持 1–3 节练习赛，默认每节 60 分钟，也可由总控逐节设置时长；每节独立排名并允许完成计时归零前已经开始的最后一圈；
- 兼容原有单节排位，并支持 1–3 节排位、按人数自动或自定义淘汰、每节独立时长、最后飞驰圈与完整排位顺位；
- 出场圈、暖胎圈、五盏红灯随机熄灭发车、抢跑自动加罚、正赛和自动方格旗；
- 排位赛和正赛自动黄旗、人工分区/全场黄旗、红旗、自动蓝旗、判罚、退赛和取消资格；
- 车手显示名、主题色，以及可由房主关闭的轻量车队展示；
- 浏览器赛事总控、实时排名、统一判罚/调查区与 JSON 审计日志；
- Windows、Linux、macOS 的 .NET 9 自托管运行方式。
- 可由用户部署到自己 Cloudflare 账号的 Durable Objects 版本。

服务端不读取游戏，也不判断轮胎是否真的更换。车手位置、圈速、维修区状态和抓地趋势由各自的 LazyForza 客户端通过官方 FH6 UDP 推导并上传。

## 版本与客户端兼容性

当前正式服务端版本为 `v0.3.0`，适用于：

- LazyForza `1.4.6`：完整支持赛中碰撞调查与可视化证据、赛后裁决、断线车手的比赛收尾、实时排行榜与本轮 HUD/进站策略交互；
- LazyForza `1.4.5`：完整支持 1–3 节练习赛与排位赛、OB 席位、练习项目、进站策略约束、统一调查与判罚、实时 HUD、维修区路线、主办方 Logo、赛道文件按需下发和赛后成绩导出；
- LazyForza `1.4.4`：协议和 `v0.1.1` 已有赛事流程兼容，但客户端没有练习项目、进站策略预测、OB 登录和本版新增的多节练习/排位交互；
- LazyForza `1.4.3`：协议和主要比赛流程兼容，但客户端没有主办方 Logo、赛道文件按需下载、练习项目、进站策略预测、OB 登录和后续维修区路线修正；
- LazyForza `1.4.2`：协议和主要比赛流程兼容。旧客户端没有服务端车队下拉框；填写的车队名与服务端配置一致时按名称加入，否则服务端会自动分配到仍有空位且当前人数较少的车队。

LazyForza `1.4.1` 及更早版本没有当前地产赛事客户端，不列入支持范围。服务端采用独立版本号；服务端代码发生变化时同步发布新版本。若后续 LazyForza 客户端仍兼容同一服务端版本，应更新该服务端 GitHub Release 的适用版本列表，不为单纯的说明更新另起服务端版本。

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
./scripts/Publish-Development.ps1 -DevelopmentLabel 20260812-dev.2
```

开发预览默认只生成 `win-x64` 独立运行包，并写入 `artifacts/development`。`-DevelopmentLabel` 只给原生开发包加版本标识，不会额外打包 Cloudflare 源码；只有明确传入 `-CloudflareLabel` 时才生成 Cloudflare 开发包。正式发行脚本会显式生成 `win-x64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64` 五个平台产物；如确有临时验证需要，仍可手动传入其他 `-Runtime`。

正式发布使用独立的服务端版本号，并在说明中列出全部已确认兼容的 LazyForza 客户端版本：

```powershell
./scripts/Publish-Release.ps1 -Version 0.3.0 -ReleaseNotesPath ./release-notes.md
```

正式脚本会复跑原生与 Cloudflare 验证，生成五个平台的自包含包与 Cloudflare 源码包，推送注释标签并创建 GitHub Release。

## Cloudflare Durable Objects

希望避免长期维护 VPS 时，可使用 `cloudflare` 目录中的 Workers + Durable Objects 实现：

[![Deploy to Cloudflare](https://deploy.workers.cloudflare.com/button)](https://deploy.workers.cloudflare.com/?url=https://github.com/Laz22y/LazyForza.RaceServer/tree/main/cloudflare)

点击按钮后登录自己的 Cloudflare 账号，确认 Worker 与 Durable Object 配置即可。部署模板完全位于 `cloudflare` 子目录，不依赖仓库其他目录；没有预先写入 Secret 时，第一次打开部署后的网页会进入首次设置流程。也可以运行 `scripts/Deploy-Cloudflare.ps1` 从本机部署。完整说明见 `cloudflare/README.md`。

## 配置要点

- `MaximumParticipants` 只能为 1–12；
- OB 不占车手名额，可在任何赛事阶段连接；只接收赛事快照，不上传遥测、圈速，也不参与准备、排名和判罚；
- `SectorCount` 必须与参赛车手导入的地产赛道分段数一致；
- 总控页面可托管不超过 1.5 MiB 的 `.lfzestate`；上传后服务端会校验包内数据，自动识别并保存赛道名称、赛道标识、地图修订和稳定特征 SHA-256。客户端已有匹配赛道时不会请求文件，缺失或不一致时才由车手确认下载；
- 排位赛冻结后和正赛结束后，总控可在浏览器本地导出完整成绩 PNG 与 UTF-8 CSV，不需要服务端生成图片；
- “仅记录警告，交总控核查”会生成带犯规内容、时间和圈数的待处理调查，不会提前创建处罚；总控可在判罚区确认、驳回、修改或取消处罚；
- 车手接收方格旗后不再进入停车罚时或通过维修区执行状态，也不会因赛后驶入维修区触发自动处罚。未执行的普通罚时直接加入完赛总时间；未执行的通过维修区处罚统一折算为可修改、可取消的 20 秒完赛加时；
- `data/current-race.json` 保存重要赛事快照；
- `data/audit-*.jsonl` 保存总控操作与重要事件；
- 未完成首次设置时，服务启动日志会明确提示；此时不要把地址公开给无关人员。

## 验证边界

自动测试覆盖 1–3 节练习赛、1 人发车、2–12 人容量、OB 只读连接与恢复、登录资料、赛道身份、排位最后飞驰圈、五盏红灯、抢跑、正赛排序、维修区状态、黄/红/蓝旗、处罚和断线恢复。4–12 人和 OB 属于确定性测试覆盖；只有完成真实 FH6 多机联机后，才可记录为实机验证。
