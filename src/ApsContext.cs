using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace bim_push
{
    public class ApsContext
    {
        private HttpService _httpService = new HttpService();

        private class Token
        {
            public AuthenticationHeaderValue token;
            
            private DateTime expirationTime;

            public Token()
            {
                string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(Secrets.APS_CLIENT_ID + ":" + Secrets.APS_CLIENT_SECRET));

            }

            public bool isExpired()
            {
                return DateTime.Now >= expirationTime;
            }
        }

        public ApsContext(string projectName, string internalCloudPath) 
        {
            // initialize the logger
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
        }


    }
}
