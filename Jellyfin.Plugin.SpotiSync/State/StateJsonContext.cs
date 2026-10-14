using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SpotiSync.State;

/// <summary>Source-generated JSON for state files and the dashboard API (camelCase, defaults omitted).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Catalog))]
[JsonSerializable(typeof(PlaylistState))]
[JsonSerializable(typeof(Overrides))]
[JsonSerializable(typeof(AuthState))]
[JsonSerializable(typeof(Jellyfin.Plugin.SpotiSync.Api.SettingsDto))]
[JsonSerializable(typeof(Jellyfin.Plugin.SpotiSync.Sync.LogLine))]
[JsonSerializable(typeof(Jellyfin.Plugin.SpotiSync.Api.StatusDto))]
[JsonSerializable(typeof(Jellyfin.Plugin.SpotiSync.Api.PlaylistPatch))]
[JsonSerializable(typeof(Jellyfin.Plugin.SpotiSync.Api.OverrideRequest))]
[JsonSerializable(typeof(Jellyfin.Plugin.SpotiSync.Api.SyncRequest))]
[JsonSerializable(typeof(Jellyfin.Plugin.SpotiSync.Api.AddPublicRequest))]
[JsonSerializable(typeof(Jellyfin.Plugin.SpotiSync.Api.ImportResultDto))]
[JsonSerializable(typeof(CatalogEntry))]
[JsonSerializable(typeof(Jellyfin.Plugin.SpotiSync.Api.LoginDto))]
[JsonSerializable(typeof(List<Jellyfin.Plugin.SpotiSync.Api.SearchResultDto>))]
public sealed partial class StateJsonContext : JsonSerializerContext
{
}
