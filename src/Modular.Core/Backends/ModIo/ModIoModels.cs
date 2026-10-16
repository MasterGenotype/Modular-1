using System.Text.Json.Serialization;

namespace Modular.Core.Backends.ModIo;

/// <summary>
/// A page of results. Every mod.io list endpoint returns this envelope.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public class ModIoPage<T>
{
    [JsonPropertyName("data")]
    public List<T> Data { get; set; } = new();

    [JsonPropertyName("result_count")]
    public int ResultCount { get; set; }

    [JsonPropertyName("result_offset")]
    public int ResultOffset { get; set; }

    [JsonPropertyName("result_limit")]
    public int ResultLimit { get; set; }

    [JsonPropertyName("result_total")]
    public int ResultTotal { get; set; }
}

/// <summary>
/// A mod.
/// </summary>
public class ModIoMod
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("game_id")]
    public long GameId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("name_id")]
    public string? NameId { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("profile_url")]
    public string? ProfileUrl { get; set; }

    [JsonPropertyName("date_updated")]
    public long? DateUpdated { get; set; }

    /// <summary>Bitfield: 1 alcohol, 2 drugs, 4 violence, 8 explicit.</summary>
    [JsonPropertyName("maturity_option")]
    public int MaturityOption { get; set; }

    [JsonPropertyName("submitted_by")]
    public ModIoUser? SubmittedBy { get; set; }

    [JsonPropertyName("logo")]
    public ModIoImage? Logo { get; set; }

    [JsonPropertyName("media")]
    public ModIoMedia? Media { get; set; }

    [JsonPropertyName("stats")]
    public ModIoStats? Stats { get; set; }

    /// <summary>The live file: what the game itself would download.</summary>
    [JsonPropertyName("modfile")]
    public ModIoModfile? Modfile { get; set; }

    [JsonPropertyName("tags")]
    public List<ModIoTag>? Tags { get; set; }
}

public class ModIoUser
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }
}

public class ModIoImage
{
    [JsonPropertyName("original")]
    public string? Original { get; set; }

    [JsonPropertyName("thumb_320x180")]
    public string? Thumb320x180 { get; set; }
}

public class ModIoMedia
{
    [JsonPropertyName("images")]
    public List<ModIoImage>? Images { get; set; }
}

public class ModIoStats
{
    [JsonPropertyName("downloads_total")]
    public long? DownloadsTotal { get; set; }

    [JsonPropertyName("subscribers_total")]
    public long? SubscribersTotal { get; set; }
}

public class ModIoTag
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>
/// One uploaded file of a mod.
/// </summary>
public class ModIoModfile
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("mod_id")]
    public long ModId { get; set; }

    [JsonPropertyName("date_added")]
    public long? DateAdded { get; set; }

    [JsonPropertyName("filesize")]
    public long? Filesize { get; set; }

    [JsonPropertyName("filehash")]
    public ModIoFilehash? Filehash { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("changelog")]
    public string? Changelog { get; set; }

    [JsonPropertyName("download")]
    public ModIoDownload? Download { get; set; }
}

public class ModIoFilehash
{
    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }
}

public class ModIoDownload
{
    /// <summary>
    /// Download link. For some games it carries a verification hash and expires at
    /// <see cref="DateExpires"/>, so it must be fetched fresh rather than stored.
    /// </summary>
    [JsonPropertyName("binary_url")]
    public string? BinaryUrl { get; set; }

    [JsonPropertyName("date_expires")]
    public long? DateExpires { get; set; }
}

/// <summary>
/// A mod another mod depends on.
/// </summary>
public class ModIoDependency
{
    [JsonPropertyName("mod_id")]
    public long ModId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("name_id")]
    public string? NameId { get; set; }
}

/// <summary>
/// The error envelope mod.io returns with every failed request.
/// </summary>
public class ModIoErrorEnvelope
{
    [JsonPropertyName("error")]
    public ModIoError? Error { get; set; }
}

public class ModIoError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("error_ref")]
    public int ErrorRef { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
