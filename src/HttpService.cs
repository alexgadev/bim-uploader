using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace bim_push
{
    public class HttpService
    {
        private readonly HttpClient _client;

        public static MediaTypeWithQualityHeaderValue acceptJson = new MediaTypeWithQualityHeaderValue("application/json");
        public static MediaTypeWithQualityHeaderValue acceptApiJson = new MediaTypeWithQualityHeaderValue("application/vnd.api+json");
        
        public static MediaTypeHeaderValue contentTypeUrlencoded = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        public static MediaTypeHeaderValue contentTypeJson = new MediaTypeHeaderValue("application/json");
        public static MediaTypeHeaderValue contentTypeApiJson = new MediaTypeHeaderValue("application/vnd.api+json");

        public class Header
        {
            private AuthenticationHeaderValue auth;
            private MediaTypeWithQualityHeaderValue accept;
            private MediaTypeHeaderValue contentType;

            public Header(AuthenticationHeaderValue auth, MediaTypeWithQualityHeaderValue accept, MediaTypeHeaderValue contentType)
            {
                this.auth = auth;
                this.accept = accept;
                this.contentType = contentType;
            }

            public bool IsAcceptFieldSet()
            {
                return this.accept != null;
            }

            public bool IsContentTypeFieldSet()
            {
                return this.contentType != null;
            }

            public AuthenticationHeaderValue GetAuthentication()
            {
                return this.auth;
            }

            public MediaTypeWithQualityHeaderValue GetAcceptField()
            {
                return this.accept;
            }

            public MediaTypeHeaderValue GetContentType()
            {
                return this.contentType;
            }
        }

        public HttpService()
        {
            HttpClientHandler handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };

            _client = new HttpClient(handler);
        }

        public async Task<string> GetAsync(string url, Header headers, Dictionary<string, string>? query = null)
        {
            if (query != null)
            {
                url += string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            }
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Header setting
            request.Headers.Authorization = headers.GetAuthentication();
            if (headers.IsAcceptFieldSet()) request.Headers.Accept.Add(headers.GetAcceptField());
            if (headers.IsContentTypeFieldSet()) request.Content.Headers.ContentType = headers.GetContentType();

            var response = await _client.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        }

        public async Task<string> PostAsync(string url, Header headers, string? body = null, string? param = null)
        {
            return null;
        }

        public async void put(string url, string headers, string? body = null, string? param = null)
        {

        }
    }
}
