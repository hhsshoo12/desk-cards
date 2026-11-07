using System;

internal static partial class Program
{
    private static void Check(bool condition, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!condition) throw new Exception($"Assertion failed (line {line})");
    }
}
