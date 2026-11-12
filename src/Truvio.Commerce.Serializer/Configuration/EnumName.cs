namespace Truvio.Commerce.Serializer.Configuration;

/// <summary>
/// Parses one enum member by its name, case-insensitively, and nothing else.
/// <para>
/// <c>Enum.TryParse</c> also accepts a number ("1") and a comma-joined list of names, which it
/// ORs together: "overwrite, keep" becomes <c>Keep</c> and "Replace,Merge" becomes <c>Merge</c>,
/// both defined members, so a malformed <c>mode</c> or <c>onExisting</c> value would pass
/// validation and run as a lifecycle nobody wrote. Config keys and document headers are single
/// words; anything else is unknown.
/// </para>
/// </summary>
internal static class EnumName
{
    public static bool TryParse<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (!string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                continue;
            result = Enum.Parse<TEnum>(name);
            return true;
        }
        return false;
    }

    public static TEnum? Parse<TEnum>(string? value) where TEnum : struct, Enum =>
        TryParse<TEnum>(value, out var result) ? result : null;
}
