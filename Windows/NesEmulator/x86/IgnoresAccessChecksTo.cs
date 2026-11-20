// The x86 JIT's tiered blocks (X86_JITTIER, I386.Jit.cs) live in a dynamic assembly that uses I386's private members.
namespace System.Runtime.CompilerServices
{
    /// <summary>Recognized by the runtime by name: the dynamic assembly carrying it may use the named assembly's non-public members.</summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName) => AssemblyName = assemblyName;
        public string AssemblyName { get; }
    }
}
