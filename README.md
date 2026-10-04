# DotaInsight

基于 WPF + MVVM 的 Dota2 赛事数据分析工具。

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

**客户端行为**：

- 启动 5 秒后静默检查 `/releases/latest`（仅 Release 构建），有新版才弹窗，可「稍后提醒 / 跳过此版本」；检查失败不打扰。
- 标题栏 ⟳ 按钮可手动检查，会明确告知「已是最新 / 有新版本 / 失败」。
- 升级方式：下载 zip → 由 ZipExtractor 覆盖程序目录 → 自动重启。用户数据在 `%LocalAppData%\DotaInsight`，不受影响。
- 版本判断依据：`csproj` 的 `<Version>`（发布时被 tag 覆盖）对比 `/releases/latest` 的 `tag_name`。

> 注：GitHub API 未认证时按 IP 限流 60 次/小时，仅启动一次检查足够用。

## 功能

- **英雄克制关系**：选择己方英雄，查看被克制 / 克制列表与胜率差柱状图（OpenDota API + 本地缓存）。
