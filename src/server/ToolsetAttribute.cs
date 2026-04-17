using System;

namespace Bimwright.Rvt.Server
{
    /// <summary>
    /// Documentation marker for which toolset a tool class belongs to. The active gate
    /// is <see cref="Program"/>.<c>RegisterToolsets</c>; this attribute exists so tests
    /// and future tooling can assert class → toolset mapping via reflection.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class ToolsetAttribute : Attribute
    {
        public string Name { get; }
        public ToolsetAttribute(string name) { Name = name; }
    }
}
