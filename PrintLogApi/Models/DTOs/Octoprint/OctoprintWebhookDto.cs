using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BrunoZell.ModelBinding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace PrintLogApi.Models.DTOs.Octoprint
{
    public class OctoprintWebhookDto
    {

        public string DeviceIdentifier { get; set; }

        public string ApiSecret { get; set; }

        public string Topic { get; set; }

        [ModelBinder(BinderType = typeof(JsonModelBinder))]
        public OctoprintWebhookExtraDto Extra { get; set; }

        [ModelBinder(BinderType = typeof(JsonModelBinder))]
        public OctoprintWebhookJobDto Job { get; set; }

        [ModelBinder(BinderType = typeof(JsonModelBinder))]
        public OctoprintWebhookMetaDto Meta { get; set; }

        public IFormFile snapshot { get; set; }

        /// <summary>
        /// The Unix Epoch timestamp that it started.
        /// </summary>
        public long CurrentTime { get; set; }

    }
}
