using FASTER.Properties;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Builds the product <see cref="IA3SMetadataStore"/>.
    /// Default: pure C# ObjectStream (<see cref="SerializerA3SMetadataStore"/>) — no JRE.
    /// Optional Advanced bridge fallback when <c>a3sEnableMetadataBridgeFallback</c> is on.
    /// </summary>
    public static class A3SMetadataStoreFactory
    {
        public static IA3SMetadataStore CreateDefault()
        {
            var preferNative = Settings.Default.a3sPreferNativeSerializer;
            var enableBridgeFallback = Settings.Default.a3sEnableMetadataBridgeFallback;
            var bridgePath = Settings.Default.a3sMetadataBridgePath;

            if (!preferNative)
            {
                // Legacy / emergency: force Java bridge for all metadata I/O.
                return new BridgeA3SMetadataStore(bridgePath);
            }

            var native = new SerializerA3SMetadataStore();
            if (!enableBridgeFallback)
                return native;

            return new PreferNativeA3SMetadataStore(
                native: native,
                bridge: new BridgeA3SMetadataStore(bridgePath),
                preferNativeReads: true,
                fallbackToBridgeOnReadError: true,
                preferNativeWrites: true);
        }

        /// <summary>True when product settings use native ObjectStream without bridge fallback.</summary>
        public static bool UsesNativeOnly()
            => Settings.Default.a3sPreferNativeSerializer
               && !Settings.Default.a3sEnableMetadataBridgeFallback;
    }
}
