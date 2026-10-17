using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintLogApi.Models;

public class CuraSetting
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [StringLength(100)]
    public string? CuraVersion { get; set; }

    [StringLength(100)]
    public string? PluginVersion { get; set; }

    //// https://docs.microsoft.com/en-us/ef/core/modeling/backing-field
    internal string? _Settings { get; set; }

    [NotMapped]
    public JsonElement Settings
    {
        get
        {
            return JsonSerializer.Deserialize<JsonElement>(string.IsNullOrEmpty(_Settings) ? "{}" : _Settings);
        }
        set
        {
            _Settings = value.ToString();
        }
    }

    public DateTimeOffset CreatedDate { get; set; }

    /// <summary>
    /// The ID of the user that first loaded this setting, thus locking it to that user.
    /// </summary>
    public long? UserId { get; set; }

    /// <summary>
    /// The slicer name the Slic3r uploader posts (its parser's <c>SlicerName</c>). Null means the
    /// Cura plugin, which does not send the field. Copied onto the print when one is created.
    /// </summary>
    [StringLength(50)]
    public string? Slicer { get; set; }

    /// <summary>
    /// The print a connector sent these settings for (#164), at most one record per print. Null for
    /// settings the Cura plugin or the Slic3r uploader posted: a print created from those does not
    /// link back to them. Deleting the print deletes the record.
    /// <para>
    /// Server-side only: <c>GET /api/Cura/settings</c> returns this entity, and the Cura plugin and
    /// the uploader read that shape, so neither member is serialized (or bound from a request).
    /// </para>
    /// </summary>
    [JsonIgnore]
    public long? PrintId { get; set; }

    [JsonIgnore]
    public Print? Print { get; set; }

    /// <summary>
    /// The OrcaSlicer plugin's install id (32 lowercase hex characters) when the upload carried one,
    /// so the plugin's browser fallback page can list that install's unclaimed uploads
    /// (<c>GET /api/Slicer/uploads/pending</c>). A bearer-style identifier, like the setting's own id.
    /// <para>
    /// Server-side only, like <see cref="PrintId"/>: never serialized by <c>GET /api/Cura/settings</c>
    /// and never bound from <c>POST /api/Cura/settings</c>.
    /// </para>
    /// </summary>
    [StringLength(32)]
    [JsonIgnore]
    public string? InstallId { get; set; }
}
