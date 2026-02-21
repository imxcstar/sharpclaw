using System;
using ToonSharp;

namespace Tinvo.Core.Serialization;

/// <summary>
/// JSON serialization utility using ToonSharp.
/// </summary>
public static class JsonSerializer
{
    public static string Serialize(object obj)
    {
        return ToonSerializer.Serialize(obj);
    }

    public static T? Deserialize<T>(string json)
    {
        return ToonSerializer.Deserialize<T>(json);
    }
}
