using System.Text;

namespace DotaInsight.Helpers;

/// <summary>
/// Valve KeyValue（.vdf）文本的极简解析器。
/// Steam 客户端的配置文件（如 config/loginusers.vdf）格式简单稳定：
/// 「键 值」或「键 { 子节点 }」的嵌套结构，不值得为此引入第三方依赖。
/// 解析按「读不出 = 没有」处理：空文本、残缺结构一律返回空节点，绝不抛异常，
/// 调用方据此自然降级（Steam 未装 / 未记住登录都表现为查不到）。
/// </summary>
public static class VdfParser
{
    /// <summary>
    /// 解析结果节点：叶子节点有 <see cref="Value"/>，小节节点是子项集合
    /// （沿 <see cref="Dictionary{TKey,TValue}"/>；Valve KeyValue 键不区分大小写，
    /// 同一字段在不同客户端版本里出现过 Timestamp / timestamp 两种写法）。
    /// </summary>
    public sealed class Node : Dictionary<string, Node>
    {
        public Node() : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        /// <summary>叶子节点的值；小节节点为 null。</summary>
        public string? Value { get; init; }

        public bool IsLeaf => Value is not null;
    }

    public static Node Parse(string? text)
    {
        var root = new Node();
        if (string.IsNullOrWhiteSpace(text))
        {
            return root;
        }

        try
        {
            var tokens = Tokenize(text);
            var pos = 0;
            FillBlock(root, tokens, ref pos);
        }
        catch
        {
            // 残缺结构按「解析失败」处理：返回已解析到的部分（通常是空），不让上层为坏文件崩掉
        }

        return root;
    }

    /// <summary>消费一个块内的「键 → 值 / 子小节」直到遇到 } 或文本结束。</summary>
    private static void FillBlock(Node block, List<object> tokens, ref int pos)
    {
        while (pos < tokens.Count)
        {
            if (tokens[pos] is '}')
            {
                pos++;
                return;
            }

            if (tokens[pos] is not string key)
            {
                // 键位置出现意外花括号（残缺文件）：跳过继续，尽量多捞一些有效键值
                pos++;
                continue;
            }

            pos++;
            if (pos >= tokens.Count)
            {
                return;
            }

            if (tokens[pos] is '{')
            {
                pos++;
                var child = new Node();
                FillBlock(child, tokens, ref pos);
                block[key] = child;
            }
            else if (tokens[pos] is string value)
            {
                pos++;
                block[key] = new Node { Value = value };
            }
        }
    }

    /// <summary>词法切分：引号字符串（含 VDF 转义）、裸词、花括号。</summary>
    private static List<object> Tokenize(string text)
    {
        var tokens = new List<object>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            // Steam 的文件偶尔带 UTF-8 BOM，一并当空白跳过
            if (c == '\uFEFF' || char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c is '{' or '}')
            {
                tokens.Add(c);
                i++;
                continue;
            }

            if (c == '"')
            {
                tokens.Add(ReadQuoted(text, ref i));
                continue;
            }

            // VDF 允许不带引号的值：读到空白或花括号为止
            var start = i;
            while (i < text.Length
                   && !char.IsWhiteSpace(text[i])
                   && text[i] is not ('{' or '}'))
            {
                i++;
            }

            tokens.Add(text[start..i]);
        }

        return tokens;
    }

    /// <summary>读取一个带引号的字符串并解码转义；引号未闭合时把剩余文本当值返回。</summary>
    private static string ReadQuoted(string text, ref int i)
    {
        i++; // 跳过开头的 "
        var sb = new StringBuilder();
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                i++;
                return sb.ToString();
            }

            if (c == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                sb.Append(next switch
                {
                    '\\' => '\\',
                    '"' => '"',
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    // 未知转义按字面保留，与 Valve 客户端的宽松写回行为对齐
                    _ => next
                });
                i += 2;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
