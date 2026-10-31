using System;

namespace FASTER.Services.A3S
{
    /// <summary>Raised when the headless metadata bridge is missing or fails.</summary>
    public class A3SBridgeException : Exception
    {
        public A3SBridgeException(string message)
            : base(message)
        {
        }

        public A3SBridgeException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>Bridge binary was not found at the resolved path.</summary>
    public sealed class A3SBridgeNotFoundException : A3SBridgeException
    {
        public string ResolvedPath { get; }

        public A3SBridgeNotFoundException(string resolvedPath)
            : base(
                "A3S metadata bridge not found. Classic .a3s ObjectStream files require the headless bridge " +
                $"(see FASTER/Services/A3S/BRIDGE.md). Resolved path: '{resolvedPath}'. " +
                "Set Settings → Advanced → metadata bridge path, or bundle the companion binary next to FASTER.")
        {
            ResolvedPath = resolvedPath;
        }
    }
}
