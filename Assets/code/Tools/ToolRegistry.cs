using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Prowl.Runtime;

namespace Slopbox;

/// <summary>
/// Every tool in the game, found by reflection: any <see cref="BaseTool"/> subclass with a
/// <see cref="ToolAttribute"/>. There is one instance of each, so settings persist for the session.
/// </summary>
public sealed class ToolRegistry
{
    private readonly List<BaseTool> _tools = new();

    public IReadOnlyList<BaseTool> All => _tools;
    public IReadOnlyList<string> Categories { get; }

    public ToolRegistry()
    {
        foreach (Type type in typeof(BaseTool).Assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(BaseTool).IsAssignableFrom(type)) continue;
            ToolAttribute info = type.GetCustomAttribute<ToolAttribute>();
            if (info == null) continue;

            try
            {
                var tool = (BaseTool)Activator.CreateInstance(type);
                tool.Info = info;
                _tools.Add(tool);
            }
            catch (Exception e)
            {
                Debug.LogError($"Could not create tool {type.Name}: {e.Message}");
            }
        }

        _tools.Sort((a, b) =>
        {
            int c = string.Compare(a.Info.Category, b.Info.Category, StringComparison.Ordinal);
            if (c != 0) return c;
            c = a.Info.Order.CompareTo(b.Info.Order);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        Categories = _tools.Select(t => t.Info.Category).Distinct().ToList();
    }

    public IEnumerable<BaseTool> InCategory(string category) => _tools.Where(t => t.Info.Category == category);

    public T Get<T>() where T : BaseTool => _tools.OfType<T>().FirstOrDefault();
}
