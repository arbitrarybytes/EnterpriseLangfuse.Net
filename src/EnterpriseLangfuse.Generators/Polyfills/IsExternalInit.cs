// netstandard2.0 predates the init-accessor marker type, which the compiler requires for records.
// Declaring it here is the sanctioned polyfill; it is erased at runtime.

namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}
