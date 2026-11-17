namespace PrintLogApi.Email;

public static class EmailMasking
{
    /// <summary>First character, a fixed-width mask, then the domain: "christopher@gmail.com" → "c•••@gmail.com".</summary>
    public static string Mask(string email)
    {
        var at = email.IndexOf('@');
        return at <= 0 ? "•••" : email[0] + "•••" + email[at..];
    }
}
