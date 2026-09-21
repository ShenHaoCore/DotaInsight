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

## 功能

- **英雄克制关系**：选择己方英雄，查看被克制 / 克制列表与胜率差柱状图（OpenDota API + 本地缓存）。
