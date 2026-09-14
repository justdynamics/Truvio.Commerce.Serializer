using Dynamicweb.Data;

namespace Truvio.Commerce.Serializer.Infrastructure;

/// <summary>
/// Binds column values into a <see cref="CommandBuilder"/> without binding NULL as a parameter.
/// CommandBuilder reuses one parameter for every value with the same DbType and text, and
/// <see cref="DBNull.Value"/> renders as "": a NULL bound after an empty string took the ''
/// parameter (SQL Server then stores 1900-01-01 in datetime and 0 in numeric columns), and an
/// empty string bound after a NULL was stored as NULL (issue #18). A NULL is therefore always
/// written as the <c>NULL</c> literal.
/// </summary>
public static class CommandBuilderValues
{
    public static bool IsNull(object? value) => value is null or DBNull;

    /// <summary>
    /// Appends <paramref name="prefix"/> followed by the value: the <c>NULL</c> literal for a null
    /// or <see cref="DBNull"/>, otherwise a parameter. <paramref name="prefix"/> is SQL text and
    /// must not contain format placeholders.
    /// </summary>
    public static void AddValue(CommandBuilder cb, string prefix, object? value)
    {
        if (IsNull(value))
            cb.Add(prefix + "NULL");
        else
            cb.Add(prefix + "{0}", value);
    }
}
