# DotaInsight

[![Release](https://img.shields.io/github/v/release/ShenHaoCore/DotaInsight?label=release)](https://github.com/ShenHaoCore/DotaInsight/releases/latest)
[![CI](https://github.com/ShenHaoCore/DotaInsight/actions/workflows/dotnet.yml/badge.svg)](https://github.com/ShenHaoCore/DotaInsight/actions/workflows/dotnet.yml)

基于 WPF + MVVM 的 Dota2 赛事数据分析工具。

## 下载

**最新版**：[Releases · latest](https://github.com/ShenHaoCore/DotaInsight/releases/latest)

在 Releases 页下载 `DotaInsight-<版本>-win-x64.zip`，**解压后双击 `DotaInsight.exe` 即可运行**。

**免安装**：绿色版，解压即用；不写注册表，也不往程序目录落文件——缓存、日志、账号全部在
`%LocalAppData%\DotaInsight`。

- win-x64 **自包含单文件**，无需预装 .NET 运行时
- 系统要求：Windows 10 / 11（64 位）
- 需要 **WebView2 运行时**（Win11 自带；Win10 装了 Edge 即具备）。缺失时应用照常运行，
  仅英雄头图的动态视频静默退化为静态封面
- 建议解压到**有写入权限**的目录（别放 `C:\Program Files`）：应用内升级要就地覆盖 exe，
  目录不可写时会退化为「打开发布页手动下载」
- 用户数据在 `%LocalAppData%\DotaInsight`，覆盖升级不受影响
- 应用内可自动升级：启动后静默检查，标题栏 ⟳ 也可手动检查

> 为什么不直接放裸 `.exe`：单文件 exe 有 207 MB，而我们发布的 zip 里就它一个文件、
> 压缩后只有 85 MB；且应用内升级**必须**走 zip（见下方「发布与升级」），
> 保持单一资产可让手动下载与自动升级取同一个文件，避免下错。

## 技术栈

- .NET 8 + WPF
- CommunityToolkit.Mvvm
- Microsoft.Extensions.DependencyInjection
- Autoupdater.NET.Official
- LiveCharts2
- Serilog + Async + Exceptions
- HttpClient + Polly
- LiteDB

## 运行

```bash
dotnet run --project src/DotaInsight/DotaInsight.csproj
```

## 发布与升级

以 GitHub Releases 作为唯一的发布与升级通道，不需要额外托管 AppCast XML。

**发一版**（标签号即版本号，会注入程序集）：

```bash
git tag v0.2.0 && git push origin v0.2.0
```

`.github/workflows/release.yml` 会发布自包含单文件 exe（win-x64，约 207 MB），zip 资产命名固定为
`DotaInsight-<tag>-win-x64.zip` —— 客户端按这个后缀找安装包，改名会导致客户端取不到。

> 不要额外上传裸 `.exe` 资产：Autoupdater.NET 只对 `.zip` 下载地址做「解压覆盖」，
> 非 zip 地址会被当成安装器去执行——裸 exe 被跑起来只会启一个新实例，并不会替换任何文件，
> 升级会静默失败。zip 是唯一的正确升级载体。

**客户端行为**：

- 启动 5 秒后静默检查 `/releases/latest`（仅 Release 构建），有新版才弹窗，可「稍后提醒 / 跳过此版本」；检查失败不打扰。
- 标题栏 ⟳ 按钮可手动检查，会明确告知「已是最新 / 有新版本 / 失败」。
- 升级方式：下载 zip → 由 ZipExtractor 覆盖程序目录 → 自动重启。用户数据在 `%LocalAppData%\DotaInsight`，不受影响。
- 版本判断依据：`csproj` 的 `<Version>`（发布时被 tag 覆盖）对比 `/releases/latest` 的 `tag_name`。

> 注：GitHub API 未认证时按 IP 限流 60 次/小时，仅启动一次检查足够用。

## 功能

- **英雄克制关系**：选择己方英雄，查看被克制 / 克制列表与胜率差柱状图（OpenDota API + 本地缓存）。
