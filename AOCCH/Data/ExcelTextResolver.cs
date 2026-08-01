using System;
using System.Reflection;
using Lumina.Text.ReadOnly;

namespace AOCCH.Data;

internal static class ExcelTextResolver
{
    private const BindingFlags PropertyFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

    public static string ResolvePropertyText<T>(T row, params string[] propertyNames)
        where T : struct
    {
        object boxedRow = row;
        return ResolvePropertyText(boxedRow, propertyNames);
    }

    public static string ResolvePropertyText(object row, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var property = row.GetType().GetProperty(propertyName, PropertyFlags);
            if (property == null)
            {
                continue;
            }

            var text = CoerceText(property.GetValue(row));
            if (text.Length > 0)
            {
                return text;
            }
        }

        return string.Empty;
    }

    public static string ResolvePropertyTemplate<T>(T row, params string[] propertyNames)
        where T : struct
    {
        object boxedRow = row;
        foreach (var propertyName in propertyNames)
        {
            var property = boxedRow.GetType().GetProperty(propertyName, PropertyFlags);
            if (property == null)
            {
                continue;
            }

            var value = property.GetValue(boxedRow);
            if (value is ReadOnlySeString seString)
            {
                return seString.ToString().Trim();
            }

            var text = CoerceText(value);
            if (text.Length > 0)
            {
                return text;
            }
        }

        return string.Empty;
    }

    public static string CoerceText(object? value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        if (value is string text)
        {
            return text.Trim();
        }

        if (value is ReadOnlySeString seString)
        {
            return seString.ExtractText().Trim();
        }

        var getText = value.GetType().GetMethod("GetText", Type.EmptyTypes);
        if (getText != null)
        {
            if (getText.Invoke(value, null) is string resolved)
            {
                return resolved.Trim();
            }
        }

        var extractText = value.GetType().GetMethod("ExtractText", Type.EmptyTypes);
        if (extractText != null)
        {
            if (extractText.Invoke(value, null) is string extracted)
            {
                return extracted.Trim();
            }
        }

        return value.ToString()?.Trim() ?? string.Empty;
    }
}
