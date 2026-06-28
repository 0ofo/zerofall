using System;

namespace ZeroFall.Base.AiTools;

[AttributeUsage(AttributeTargets.Method)]
public class AiToolAttribute(string name, string description) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; } = description;
}

[AttributeUsage(AttributeTargets.Parameter)]
public class ToolParamAttribute(string description) : Attribute
{
    public string Description { get; } = description;
    public bool Required { get; set; } = true;
}
