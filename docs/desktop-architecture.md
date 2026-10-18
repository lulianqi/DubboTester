# DubboTester 桌面端改造设计

## 目标

在保留现有 Web 与 Docker 运行方式的前提下，增加 Windows 和 macOS 的 .NET MAUI Blazor Hybrid 桌面应用。桌面窗口使用 `BlazorWebView` 加载共享 Razor UI，不依赖外部浏览器。

## 项目边界

```text
src/apps/DubboTester.Web/DubboTester.csproj (Web)
├── DubboTester.Hosting
├── DubboTester.UI
└── DubboTester.Core

src/apps/DubboTester.Desktop (MAUI)
├── DubboTester.Hosting（仅 Windows）
├── DubboTester.UI
└── DubboTester.Core
```

- `Core` 保存与宿主无关的模型和业务服务。
- `UI` 是 Razor Class Library，不设置 Web 专属 render mode。
- `Hosting` 保存 Web/Docker 使用的 ASP.NET Core 与 MCP HTTP 传输适配。
- `Desktop` 在 macOS 使用官方 MCP SDK 的低层 Streamable HTTP transport 和
  本机 `HttpListener`，避免 Mac Catalyst 引入不可用的
  `Microsoft.AspNetCore.App` runtime pack；Windows 使用 Kestrel，避免未打包应用
  绑定 `HttpListener` 时要求用户预先配置 HTTP.sys URL ACL。
- Web 与 Desktop 各自拥有入口文件，互不引用。

## Desktop 运行模型

桌面应用启动后：

1. 创建 MAUI 原生窗口和 `BlazorWebView`。
2. 通过 MAUI DI 注册 `Core` 服务并加载 `UI` 页面。
3. 在应用进程内启动仅绑定 `localhost:5154` 的 MCP Streamable HTTP Endpoint。
4. UI 与 MCP Endpoint 共享 MCP 配置、Dubbo 目录缓存和 Dubbo 客户端生命周期。
5. 关闭桌面窗口时停止 MCP Endpoint 并释放 Dubbo 连接。

桌面配置保存在 MAUI `FileSystem.AppDataDirectory` 下的 `App_Data` 子目录中。
该路径由系统管理；macOS 沙盒版会位于应用容器内，Windows 未打包版会位于当前
用户的本地应用数据目录。Web/Docker 仍使用内容根目录下的 `App_Data`。

## 当前改造阶段

已完成：

- Core、UI、Hosting 项目拆分。
- Web 入口改为复用 Hosting 注册和 MCP Endpoint 映射。
- MCP 配置路径与请求地址读取改为宿主抽象。
- 共享页面和静态资源迁移至 RCL。
- Web 构建、测试、发布与页面路由验证。
- 创建只包含 Windows 与 Mac Catalyst target 的 `DubboTester.Desktop`。
- 桌面端复用共享 Razor 页面、业务服务和静态资源。
- 增加桌面配置路径实现和窗口生命周期管理。
- 接入进程内 MCP Endpoint，桌面 UI 与 MCP 调用共享同一服务实例。
- 在 macOS 上生成并启动 Mac Catalyst Debug 应用，验证共享页面导航和配置持久化。
- 验证桌面进程内 MCP Endpoint 的鉴权、`initialize` 与 `tools/list` Streamable HTTP 请求。
- 增加 GitHub Actions 桌面构建工作流，生成 macOS universal `.app` 和 Windows x64
  self-contained 免安装压缩包。
- 已在 GitHub Actions 中完成 Windows x64 与 macOS universal Release 构建及产物上传验证。

待完成：

- 为正式发布工作流接入 Windows/macOS 签名证书，并完成 macOS 公证。

## 本机构建准备

macOS 只需要安装 Mac Catalyst workload，无需同时下载 Android 和 iOS 运行包：

```bash
sudo dotnet workload install maui-maccatalyst
```

Windows 构建机对应安装 `dotnet workload install maui-windows`。网络不稳定时可以为
安装命令增加 `--disable-parallel`，让工作负载包顺序下载。

如果官方 NuGet 源连接不稳定，可以让镜像优先、官方源补齐镜像缺少的包：

```bash
sudo dotnet workload install maui-maccatalyst \
  --disable-parallel \
  --skip-manifest-update \
  --ignore-failed-sources \
  --source https://mirrors.cloud.tencent.com/nuget/ \
  --source https://api.nuget.org/v3/index.json
```

macOS 构建还需要选中完整 Xcode：

```bash
sudo xcode-select --switch /Applications/Xcode.app/Contents/Developer
```

可用环境中也可以只对当前命令指定 Xcode，避免修改系统级设置：

```bash
DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer \
  dotnet build src/apps/DubboTester.Desktop/DubboTester.Desktop.csproj -c Debug
```

Mac Catalyst workload 对 Xcode 版本有严格要求；如果构建报告版本不匹配，需要升级
Xcode/macOS 或使用与当前 Xcode 匹配的 .NET SDK/workload。为了便于迁移期本地调试，
本项目仅在 Mac Catalyst Debug 配置中关闭严格版本校验并使用 `SdkOnly` 链接；当前
Xcode 16.3 已验证可以生成和启动 Debug 应用，但正式 Release 仍应使用 workload 要求
的 Xcode 版本。Web、测试和 Docker 项目不依赖 MAUI workload，仍可独立构建。
