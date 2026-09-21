# -*- coding: utf-8 -*-
import json
from pathlib import Path

src = Path(r"C:\Users\Administrator\.cursor\projects\e-Repos-DotaInsight\agent-tools\c30e7f5a-f0df-44e7-959e-b3bf5496e02d.txt")
d = json.loads(src.read_text(encoding="utf-8"))
heroes = sorted(d["result"]["heroes"], key=lambda h: h["id"])

lines = [
    "namespace DotaInsight.Helpers;",
    "",
    "/// <summary>",
    "/// 国服官方英雄中文名内置回退表（离线可用）。",
    "/// </summary>",
    "public static class HeroChineseNames",
    "{",
    "    /// <summary>英雄 Id -> 简体中文名。</summary>",
    "    public static IReadOnlyDictionary<int, string> ById { get; } = new Dictionary<int, string>",
    "    {",
]
for h in heroes:
    name = h["name_loc"].replace("\\", "\\\\").replace('"', '\\"')
    lines.append(f'        {{ {h["id"]}, "{name}" }},')
lines.append("    };")
lines.append("")
lines.append("    /// <summary>内部名 -> 简体中文名。</summary>")
lines.append(
    "    public static IReadOnlyDictionary<string, string> ByInternalName { get; } ="
)
lines.append(
    "        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)"
)
lines.append("    {")
for h in heroes:
    name = h["name_loc"].replace("\\", "\\\\").replace('"', '\\"')
    lines.append(f'        {{ "{h["name"]}", "{name}" }},')
lines.append("    };")
lines.append("}")

out = Path(r"E:\Repos\DotaInsight\src\DotaInsight\Helpers\HeroChineseNames.cs")
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text("\n".join(lines) + "\n", encoding="utf-8")
print(f"wrote {out} count={len(heroes)}")
