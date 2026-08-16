using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.DTOs.User
{
    /// <summary>
    /// Used to wrap new Urls in a json object.
    /// </summary>
    public class UserUrlDto
    {
        public string? Url { get; set; }
    }
}
