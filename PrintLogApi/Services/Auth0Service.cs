using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using PrintLogApi.Models.DTOs;

namespace PrintLogApi.Services
{
    public class Auth0Service : IAuth0Service
    {
        private readonly IHttpClientFactory _clientFactory;
        private readonly IConfiguration _configuration;

        public Auth0Service(IHttpClientFactory clientFactory, IConfiguration configuration)
        {
            _clientFactory = clientFactory;
            _configuration = configuration;
        }

        /// <summary>
        ///   Gets an access token needed to interact with the Auth0 Management Apis
        /// </summary>
        /// <returns></returns>
        public async Task<string> GetManagementApiBearerToken()
        {
            var domain = $"https://{_configuration["Auth0Management:Domain"]}/oauth/token";

            var content = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", _configuration["Auth0Management:ClientId"]),
                new KeyValuePair<string, string>("client_secret", _configuration["Auth0Management:ClientSecret"]),
                new KeyValuePair<string, string>("audience", $"https://{_configuration["Auth0Management:Domain"]}/api/v2/"),
            };
            using var client = _clientFactory.CreateClient();
            using var body = new FormUrlEncodedContent(content);
            using var request = new HttpRequestMessage(HttpMethod.Post, domain);
            request.Content = body;

            var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync();
                Jwt token = JsonSerializer.Deserialize<Jwt>(result);

                return $"Bearer {token.AccessToken}";

            }
            else
            {
                throw new Exception();
            }


        }

        /// <summary>
        ///     Gets a user's information from Auth0
        /// </summary>
        /// <param name="oauthUserId"></param>
        /// <returns></returns>
        public async Task GetUser(string oauthUserId)
        {
            var requestUrl = $"https://{_configuration["Auth0Management:Domain"]}/api/v2/users/{oauthUserId}";

            var token = await GetManagementApiBearerToken();

            using var client = _clientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Add("Authorization", token);

            var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync();


                return;

            }
            else
            {
                throw new Exception();
            }
        }

        /// <summary>
        ///   Permanently delete a user from Auth0 by their oauth id.
        /// </summary>
        /// <param name="oauthUserId"></param>
        /// <returns></returns>
        public async Task DeleteUser(string oauthUserId)
        {
            var requestUrl = $"https://{_configuration["Auth0Management:Domain"]}/api/v2/users/{oauthUserId}";

            var token = await GetManagementApiBearerToken();

            using var client = _clientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Delete, requestUrl);
            request.Headers.Add("Authorization", token);

            var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                return;
            }
            else
            {
                throw new Exception();
            }
        }
    }
}
