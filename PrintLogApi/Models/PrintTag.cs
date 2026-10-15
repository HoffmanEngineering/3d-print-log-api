namespace PrintLogApi.Models;

/// <summary>
/// One print carrying one tag. A join row with no audit columns of its own: both ends are the
/// same user's, and the row means nothing beyond the pair.
/// </summary>
public class PrintTag
{
    public long PrintId { get; set; }
    public Print Print { get; set; } = null!;

    public Guid TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}
