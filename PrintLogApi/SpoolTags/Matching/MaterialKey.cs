namespace PrintLogApi.SpoolTags.Matching;

public static class MaterialKey
{
    /// <summary>Uppercases and removes spaces, <c>-</c> and <c>_</c>, so "PLA +" and "pla+" agree.</summary>
    public static string Normalize(string? material)
    {
        if (string.IsNullOrEmpty(material))
        {
            return "";
        }
        return string.Concat(material.Where(c => c is not (' ' or '-' or '_'))).ToUpperInvariant();
    }
}
