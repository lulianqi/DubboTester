# DubboTester

从 `testengineer/independent-pages/dubbo` 独立出来的 .NET 10 Blazor Server Dubbo 调试工具。

## 技术与调用方式

- .NET 10 / Blazor Interactive Server
- NuGet `DubboNet` **2.0.0**
- ZooKeeper 用于服务发现和元数据读取
- 业务请求通过 Dubbo2 TCP + Hessian2 `$invoke` 泛化调用发送，不使用 Telnet `invoke`
- 可选注册中心负载均衡调用，或指定 Provider 直连调用

`DubboNet` 在元数据中心不可用时可能在包内部用 Provider Telnet `ls -l` 读取一次方法签名；这只用于签名发现，不用于业务调用。使用界面中的精确 Java 参数类型即可跳过签名自动发现。

DubboNet 2.0.0 可读取元数据中心 `FullServiceDefinition.types`。当方法包含 POJO、集合、数组、枚举等结构型参数时，可以在“Java 签名”区域查看参数结构，并按该结构生成可编辑的请求 JSON 示例。类型结构仅由元数据中心提供；Telnet 签名来源不包含这部分信息。

Dubbo 2.7.x 可能把 `String`、`Long`、`Integer` 等 JDK 类型的内部实现字段也写入 `properties`，例如 `String.value`、`String.hash`。DubboTester 会按字段声明类型识别这些标量/包装类型，不会将其内部字段当作业务请求字段；业务 POJO 自身声明的同名字段仍会正常保留。

## 运行

```bash
dotnet restore src/apps/DubboTester.Web/DubboTester.csproj
dotnet run --project src/apps/DubboTester.Web/DubboTester.csproj
```

默认开发地址见 `Properties/launchSettings.json`。首次打开后，在设置里填写 ZooKeeper 地址并刷新服务目录。

## 项目结构

```text
src/
├── apps/
│   ├── DubboTester.Web/       # Web 与 Docker 启动入口
│   └── DubboTester.Desktop/   # Windows/macOS MAUI 桌面入口
└── shared/
    ├── DubboTester.Core/      # Dubbo、缓存、压测和 MCP 核心业务
    ├── DubboTester.Hosting/   # ASP.NET Core 与 MCP HTTP 宿主适配
    └── DubboTester.UI/        # Web 与桌面端共享 Razor UI
tests/
└── DubboTester.Tests/         # 核心逻辑测试
```

解决方案中按相同职责划分为 `Apps`、`Shared` 和 `Tests` 三个逻辑目录。
仓库根目录只保留解决方案、构建部署文件和项目级文档。

桌面版位于同一个解决方案和仓库中，但拥有独立入口。它通过 `BlazorWebView`
直接加载共享页面，并在进程内提供 `http://localhost:5154/mcp`。Web 与 Docker
继续只发布 `src/apps/DubboTester.Web/DubboTester.csproj`，不会包含 MAUI 桌面运行时。详细设计见
[`docs/desktop-architecture.md`](docs/desktop-architecture.md)。

### 桌面版开发运行

安装 .NET 10 Mac Catalyst workload 后，在 macOS 上运行：

```bash
sudo dotnet workload install maui-maccatalyst
DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer \
  dotnet build src/apps/DubboTester.Desktop/DubboTester.Desktop.csproj -c Debug
```

Debug 构建产物位于
`src/apps/DubboTester.Desktop/bin/Debug/net10.0-maccatalyst/<runtime>/Dubbo Tester.app`。
当前项目的 Debug 配置允许在旧版 Xcode 上进行迁移验证，并采用 `SdkOnly` 链接；正式
Release 打包仍会执行 Mac Catalyst workload 对 Xcode 版本的严格校验。

Windows 使用同一个项目，目标框架会自动切换为
`net10.0-windows10.0.19041.0`，构建机安装 `maui-windows` workload 即可。桌面配置
保存在操作系统应用数据目录，和 Web/Docker 的 `App_Data` 相互独立。

仓库中的 `Build desktop apps` GitHub Actions 工作流会在 `release1.3` 分支更新、
相关 Pull Request 或手动触发时分别构建：

- 同时支持 Intel 与 Apple Silicon 的未签名 macOS `.app` 压缩包。
- 包含 .NET 与 Windows App SDK 运行组件的 Windows x64 免安装压缩包。

未签名产物用于开发验证。面向普通用户正式发布 macOS 版本时，仍需要 Apple Developer
证书、Developer ID 签名和公证；Windows 后续也可以在该工作流基础上增加证书签名或
MSIX 打包。

### Docker

```bash
docker build -t dubbo-tester .
docker run --rm \
  -p 5154:8080 \
  -v dubbo-tester-data:/app/App_Data \
  --name dubbo-tester \
  dubbo-tester
```

启动后访问 `http://localhost:5154`。命名卷用于持久化 MCP 配置；容器需要能够访问所配置的 ZooKeeper 和 Dubbo Provider 地址。

## 请求参数约定

- 无参数：`[]` 或留空
- 单个对象参数：`{"id": 1}`
- 多个参数：`["abc", 123, true]`
- 单个 List 参数：使用外层参数数组，例如 `[[1, 2, 3]]`

直连 Provider 模式应填写精确 Java 参数类型；注册中心模式可由 DubboNet 从元数据解析，也可以显式填写以绕过元数据探测。

## 压力测试

点击“发送”右侧的小箭头，选择“压力测试”进入独立页面。压测按“并发用户数 N × 每用户发送次数 M”运行；每个用户顺序发送自己的 M 次请求，所有用户并发执行。一次压测会复用同一个 `DubboClient`（直连模式复用同一个原生 Dubbo 连接），不会把单次请求结果写入历史记录或数据库。

请求 JSON 模板支持以下内置变量，用户序号和发送序号均从 1 开始：

- `{{user}}` / `{{userIndex}}`：当前用户序号
- `{{iteration}}` / `{{iterationIndex}}`：当前用户的发送序号
- `{{randomInt}}` / `{{randomInt:1:1000}}`：随机整数
- `{{randomString}}` / `{{randomString:12}}`：随机字符串
- `{{guid}}`：无连字符 GUID
- `{{timestamp}}`：`yyyy-MM-dd HH:mm:ss` 当前时间
- `{{unixTimestamp}}`：Unix 毫秒时间戳

页面实时显示完成进度、平均响应时间、TPS、成功率、P95、P99、吞吐和响应时间趋势。超过 20,000 个已完成请求时，运行中的 P95/P99 使用最多 20,000 个随机样本降低统计开销；压测结束后会使用全部请求耗时精确重算最终分位数。

负载配置还支持两个毫秒级间隔：

- 阶梯启动间隔：相邻两个用户的启动时间差；默认 `0`，表示全部用户同时启动。
- 请求间隔：同一用户相邻两次请求之间的等待时间；默认 `0`，表示连续发送。

趋势图支持鼠标悬停查看对应采样时刻的即时 TPS、平均响应、P95 和 P99。运行明细默认只显示失败请求，也可切换为显示全部请求；明细按每页 100 条分页。默认不保存请求和响应正文，开启“记录请求&响应”后会在本次页面内存中保存参数化后的请求和完整响应，并提供独立列、悬浮提示及详情展开查看，不会写入历史记录或数据库。

请求模板下方可以配置可选的业务断言。断言支持响应正文包含指定内容，以及通过 JSONPath（例如 `$.code`、`$.data.items[0].id`）读取字段后判断存在、不存在、等于、不等于、包含或数值大小；多个条件可以按 AND 或 OR 组合。Dubbo 调用成功但业务断言不满足时，该请求仍会计为压测失败。

压测完成或停止后可点击“导出报告”，由浏览器直接下载一个自包含 HTML 文件。报告包含实际压测配置、核心指标、趋势图和当前明细筛选结果，可离线打开；为避免生成超大文件，明细超过 20,000 条时报告会注明并仅输出前 20,000 条。

## 远程 MCP 服务

从主页顶部进入“MCP 服务”独立页面，配置 ZooKeeper 后扫描服务和方法。每个 Java 方法签名会映射为一个独立 MCP 工具；启用前必须填写工具描述、适用/触发条件和参数要求，输入 Schema 会根据元数据中的 Java 参数结构生成。全局开关与方法开关都启用后，远程 Agent 才能在 `POST /mcp` 的 `tools/list` 中看到并调用该工具。

MCP 使用官方 .NET SDK 的 Streamable HTTP 传输。鉴权默认启用，客户端连接时需要设置 `Authorization: Bearer <访问令牌>`，令牌可在管理页复制或重新生成；也可以在全局配置中关闭鉴权，让可访问该 Endpoint 的客户端直接调用。配置持久化到 `App_Data/mcp-config.json`（已从 Git 忽略），应用重启时自动恢复。调用记录只保存在进程内存中，可在页面设置保留 10–2000 条，默认 200 条。

远程访问时还需要让应用监听可达网卡并通过 HTTPS/反向代理暴露 `/mcp`；不要把管理页面或 Bearer 令牌公开给不受信任的网络。
