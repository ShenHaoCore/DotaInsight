using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotaInsight.Helpers;

/// <summary>
/// HTTP 调用公共工具：JSON 反序列化选项、用户取消判定。
/// </summary>
public static class HttpCall
{
    /// <summary>
    /// 外部接口统一 JSON 选项：属性名忽略大小写，数字允许从字符串读取，
    /// 数值字段收到 null 时容错为默认值（OpenDota 的 turn_rate 等字段可能为 null）。
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    /// <summary>
    /// 区分用户取消与 HttpClient 超时（超时也表现为 OperationCanceledException）。
    /// </summary>
    public static bool IsUserCancellation(Exception ex, CancellationToken cancellationToken)
        => ex is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        options.Converters.Add(new NullAsDefaultNumberConverter<int>());
        options.Converters.Add(new NullAsDefaultNumberConverter<long>());
        options.Converters.Add(new NullAsDefaultNumberConverter<double>());
        return options;
    }
}

/// <summary>
/// 数值容错转换器：JSON null 读为 default(T)，其余按数字 / 数字字符串解析。
/// 避免非空值类型字段（如 HeroStat.TurnRate）因接口返回 null 导致整包反序列化失败。
/// </summary>
internal sealed class NullAsDefaultNumberConverter<T> : JsonConverter<T> where T : struct
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }

        var t = typeof(T);
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return default;
            }

            if (t == typeof(int)) return (T)(object)int.Parse(text);
            if (t == typeof(long)) return (T)(object)long.Parse(text);
            if (t == typeof(double)) return (T)(object)double.Parse(text);
            throw new JsonException($"不支持的数值类型 {t}");
        }

        if (t == typeof(int)) return (T)(object)reader.GetInt32();
        if (t == typeof(long)) return (T)(object)reader.GetInt64();
        if (t == typeof(double)) return (T)(object)reader.GetDouble();
        throw new JsonException($"不支持的数值类型 {t}");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            default:
                throw new JsonException($"不支持的数值类型 {typeof(T)}");
        }
    }
}
