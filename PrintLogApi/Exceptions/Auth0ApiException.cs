using System;

namespace PrintLogApi.Exceptions
{
    /// <summary>
    /// A non-success response from the Auth0 Management API. Carries no response body so secrets
    /// are never propagated.
    /// </summary>
    public sealed class Auth0ApiException : Exception
    {
        public Auth0ApiException(string message) : base(message)
        {
        }
    }
}
