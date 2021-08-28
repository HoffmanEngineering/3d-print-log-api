using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PrintLogApi.Models.SendGrid
{
    public class SendGridEmailSenderOptions
    {
        public string ApiKey { get; set; }

        public string SenderEmail { get; set; }

        public string SenderName { get; set; }
    }
}
