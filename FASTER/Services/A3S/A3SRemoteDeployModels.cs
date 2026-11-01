using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Localhost API deploy body. Mode A: <see cref="Event"/> + optional <see cref="Repo"/>.
    /// Mode B: <see cref="AutoconfigUrl"/> (or sync URL) + <see cref="Event"/>.
    /// Launch is always performed by the host (body <c>launch</c> is accepted but forced on).
    /// </summary>
    public sealed class A3SRemoteDeployRequest
    {
        [JsonPropertyName("event")]
        public string? Event { get; set; }

        /// <summary>Repository name or id (Mode A).</summary>
        [JsonPropertyName("repo")]
        public string? Repo { get; set; }

        /// <summary>Autoconfig or sync URL (Mode B).</summary>
        [JsonPropertyName("autoconfigUrl")]
        public string? AutoconfigUrl { get; set; }

        /// <summary>Alias accepted for Mode B.</summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("profile")]
        public string? Profile { get; set; }

        /// <summary>Ignored for product path — auto-launch is always on. Kept for Discord clients.</summary>
        [JsonPropertyName("launch")]
        public bool? Launch { get; set; }

        [JsonIgnore]
        public string? ResolvedAutoconfigUrl =>
            !string.IsNullOrWhiteSpace(AutoconfigUrl) ? AutoconfigUrl
            : !string.IsNullOrWhiteSpace(Url) ? Url
            : null;
    }

    public sealed class A3SRemoteDeployResult
    {
        public string JobId { get; init; } = string.Empty;
        public string EventName { get; init; } = string.Empty;
        public string ProfileName { get; init; } = string.Empty;
        public string RepositoryName { get; init; } = string.Empty;
        public string RepositoryId { get; init; } = string.Empty;
        public int EventAddonCount { get; init; }
        public int MatchedMods { get; init; }
        public int MissingAddons { get; init; }
        public IReadOnlyList<string> MissingAddonKeys { get; init; } = System.Array.Empty<string>();
        public bool Launched { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    public sealed class A3SRemoteStatusResult
    {
        public bool ApiListening { get; init; }
        public string Bind { get; init; } = "127.0.0.1";
        public int Port { get; init; }
        public string? DefaultProfile { get; init; }
        public string? LastJobId { get; init; }
        public string? LastEvent { get; init; }
        public string? LastProfile { get; init; }
        public bool? LastLaunched { get; init; }
        public string? LastError { get; init; }
        public bool Busy { get; init; }
        public IReadOnlyList<string> Profiles { get; init; } = System.Array.Empty<string>();
        public IReadOnlyList<string> Repositories { get; init; } = System.Array.Empty<string>();
    }

    public sealed class A3SRemoteEventsResult
    {
        public string RepositoryName { get; init; } = string.Empty;
        public string AutoconfigUrl { get; init; } = string.Empty;
        public IReadOnlyList<string> Events { get; init; } = System.Array.Empty<string>();
    }

    public sealed class A3SRemoteServerControlResult
    {
        public string Action { get; init; } = string.Empty;
        public string ProfileName { get; init; } = string.Empty;
        public string ProfileId { get; init; } = string.Empty;
        public int Port { get; init; }
        public bool Ok { get; init; }
        public string Message { get; init; } = string.Empty;
        public IReadOnlyList<int> ProcessIds { get; init; } = System.Array.Empty<int>();
    }
}
