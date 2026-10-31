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
                "A3S metadata bridge JAR was requested but not found at " +
                $"'{resolvedPath}'. Classic Sync/Build/Publish normally use the native C# ObjectStream " +
                "serializer (no Java). Enable Settings → Advanced → bridge fallback only if you need the " +
                "optional Java helper, then set the bridge JAR path or place a3s-metadata-bridge-*-all.jar " +
                "under a3s-bridge/ next to FASTER (see FASTER/Services/A3S/BRIDGE.md).")
        {
            ResolvedPath = resolvedPath;
        }
    }
}
