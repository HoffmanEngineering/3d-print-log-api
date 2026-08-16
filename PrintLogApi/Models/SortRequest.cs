namespace PrintLogApi.Models;

public class SortRequest<T> where T : System.Enum
{
    public SortDirection SortDirection { get; set; }

    public T SortColumn { get; set; } = default!;
}
