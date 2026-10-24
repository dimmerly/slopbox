using System;
using System.Collections.Generic;
using System.Reflection;
using Prowl.Echo;

namespace Slopbox;

internal static class ToolSettings
{
    // Read only declared tool settings, never arbitrary object types supplied by a peer.
    public static string Write(BaseTool tool)
    {
        var values = EchoObject.NewCompound();
        foreach (FieldInfo field in tool.GetType().GetFields())
            if (field.IsDefined(typeof(ToolSettingAttribute)))
                values[field.Name] = Serializer.Serialize(field.GetValue(tool));
        return values.WriteToString();
    }

    public static void Read(BaseTool tool, string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 16384) return;
        var values = EchoObject.ReadFromString(text);
        foreach (FieldInfo field in tool.GetType().GetFields())
            if (field.IsDefined(typeof(ToolSettingAttribute)) && values.TryGet(field.Name, out var value))
                field.SetValue(tool, Serializer.Deserialize(value, field.FieldType));
    }
}
