# 地产赛事协议生成

`race-protocol.schema.json` 是地产赛事协议模型的唯一源。它声明协议常量、消息类型、枚举、DTO 字段、默认值，以及客户端、原生服务端和 TypeScript 目标之间少量有意保留的名称或字段差异。

在 `LazyForza` 与 `LazyForza.RaceServer` 两个仓库并列存在时，从 RaceServer 根目录运行：

```powershell
node scripts/generate-protocol.mjs
```

生成器会更新客户端 `EstateRaceProtocol.g.cs`、服务端 `RaceProtocolModels.g.cs` 和 Cloudflare `protocol.generated.ts`。生成文件需要提交，但不得手工编辑。只有独立构建 RaceServer 或 Cloudflare 包时才使用 `--skip-client`；完整跨仓库协议修改必须生成并检查客户端产物。

```powershell
node scripts/generate-protocol.mjs --check
cd cloudflare
npm run check:generated
```

Schema 使用语言无关的类型表达式：`string`、`boolean`、`int32`、`int64`、`float64`、`uuid`、`timestamp`、`nullable<T>`、`array<T>`、`map<K,V>`、`enum:Name` 和 `model:Name`。`targets` 指定各语言中的类型名称；`types`、`names`、`defaults`、`required` 与 `exclude` 只用于记录现有兼容差异，不能用来绕过应当统一的协议设计。

序列化、输入清洗和运行时校验不是数据模型，继续保留在各端手写文件中。修改 Schema 后仍需检查旧客户端兼容、双权威逻辑、路由、Web 和相应测试。
