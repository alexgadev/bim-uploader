using Newtonsoft.Json.Linq;
using System;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;


namespace bim_push
{
    public class ApsContext
    {
        private HttpService _httpService;
        private Logger _logHandle;

        private class AuthenticationToken
        {
            public string Value;
            
            private DateTime _expiresAt;

            public AuthenticationToken(ApsContext _owner)
            {
                string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(Secrets.APS_CLIENT_ID + ":" + Secrets.APS_CLIENT_SECRET));
                AuthenticationHeaderValue authString = new AuthenticationHeaderValue("Basic", token);
                
                HttpService.Header header = new HttpService.Header(authString, HttpService.acceptJson, HttpService.contentTypeUrlencoded);

                var body = new { grant_type = "client_credentials", scope = "data:read data:write" };

                var response = _owner._httpService.PostAsync(Secrets.BASE_DOMAIN + "authentication/v2/token",
                                                            header,
                                                            data: new Dictionary<string, string>
                                                            {
                                                                ["grant_type"] = "client_credentials",
                                                                ["scope"] = "data:read data:write"
                                                            });
                dynamic data = JObject.Parse(response.Result);
                
                this.Value = data.access_token;

                string secondsUntilExpiration = data.expires_in;
                this._expiresAt = DateTime.UtcNow.AddSeconds(double.Parse(secondsUntilExpiration) - 60);
            }

            public bool isExpired()
            {
                return DateTime.Now >= _expiresAt;
            }
        }

        private AuthenticationToken? token = null;

        public ApsContext(string projectName, string internalCloudPath) 
        {
            // initialize the logger
            _logHandle = new();

            // read json and populate project and hubs data structures
            // check for arguments correctness
                // check if project exists within the json
                    // obtain 2-legged token
                    // if project doesn't exist within the json config file, check for the project looping through hubs (one request only)
                // check for goal folder id inside the config json
                    // call FindFolderVars
        }

        ~ApsContext() 
        {
            // release data structures
        }

        // provide functionalities to check on token expiration
        // check token expiration before every request
        // 

        private void FindFolderVars()
        {

        }

        public void Run(string serverPath)
        {
            AuthenticationToken token = new AuthenticationToken(this);
            Console.WriteLine(token.Value);
        }


    }
}
