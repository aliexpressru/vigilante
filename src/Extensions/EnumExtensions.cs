namespace Vigilante.Extensions;

/// <summary>
/// Extension methods for enum parsing and validation
/// </summary>
public static class EnumExtensions
{
    /// <summary>
    /// Tries to parse a string value to an enum of type T.
    /// Returns null if the string is null, empty, or cannot be parsed.
    /// </summary>
    /// <typeparam name="TEnum">The enum type to parse to</typeparam>
    /// <param name="value">The string value to parse</param>
    /// <param name="ignoreCase">Whether to ignore case when parsing. Default is true.</param>
    /// <returns>The parsed enum value or null if parsing fails</returns>
    public static TEnum? TryParseEnum<TEnum>(this string? value, bool ignoreCase = true) 
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<TEnum>(value, ignoreCase, out var result))
        {
            return result;
        }

        return null;
    }

    /// <summary>
    /// Tries to parse a string value to an enum of type T with a default value.
    /// Returns the default value if the string is null, empty, or cannot be parsed.
    /// </summary>
    /// <typeparam name="TEnum">The enum type to parse to</typeparam>
    /// <param name="value">The string value to parse</param>
    /// <param name="defaultValue">The default value to return if parsing fails</param>
    /// <param name="ignoreCase">Whether to ignore case when parsing. Default is true.</param>
    /// <returns>The parsed enum value or default value if parsing fails</returns>
    public static TEnum ParseEnumOrDefault<TEnum>(this string? value, TEnum defaultValue, bool ignoreCase = true) 
        where TEnum : struct, Enum
    {
        return value.TryParseEnum<TEnum>(ignoreCase) ?? defaultValue;
    }
}
