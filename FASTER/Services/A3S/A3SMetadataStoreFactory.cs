using FASTER.Properties;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Builds the product <see cref="IA3SMetadataStore"/>: Phase 4 prefers the C# ObjectStream
    /// serializer for reads, with the Java bridge kept for writes and optional read fallback.
    /// </summary>
    public static class A3SMetadataStoreFactory
    {
        public static IA3SMetadataStore CreateDefault()
        {
            var preferNative = Settings.Default.a3sPreferNativeSerializer;
            var bridge = new BridgeA3SMetadataStore(Settings.Default.a3sMetadataBridgePath);
            if (!preferNative)
                return bridge;

            return new PreferNativeA3SMetadataStore(
                native: new SerializerA3SMetadataStore(),
                bridge: bridge,
                preferNativeReads: true,
                fallbackToBridgeOnReadError: true);
        }
    }
}
