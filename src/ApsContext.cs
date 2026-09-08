using Newtonsoft.Json;
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
        private HttpService httpService;
        private Logger logHandle;
        private AuthenticationToken? token = null;
        private ApsProject? project = null;

        private class ApsProject
        {
            private string name;
            private string hubId;
            private string projectId;
            private string folderId;
            private int objectCount;

            public ApsProject(string name, string? folderId = null, int? objectCount = null)
            { //TODO: add hub and project to constructor
                this.name = name;
                this.folderId = folderId;
                this.objectCount = objectCount == null ? -1 : (int)objectCount;
            }

            public string GetName() { return this.name; }
            public string GetHubId() { return this.hubId; }
            public void SetProjectId(string id) { this.projectId = id; }
            public string GetProjectId() { return this.projectId; }
            public void SetFolderId(string id) {  this.folderId = id; }
            public string GetFolderId() {  return this.folderId; }
            public void SetObjectCount(int count) { this.objectCount = count; }
            public int GetObjectCount() { return this.objectCount; }
        }

        private class AuthenticationToken
        {
            public string Value;
            
            private DateTime expiresAt;

            public AuthenticationToken(ApsContext _owner)
            {
                string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(Secrets.APS_CLIENT_ID + ":" + Secrets.APS_CLIENT_SECRET));
                AuthenticationHeaderValue authString = new AuthenticationHeaderValue("Basic", token);
                
                HttpService.Header header = new HttpService.Header(authString, HttpService.acceptJson, HttpService.contentTypeUrlencoded);

                var body = new { grant_type = "client_credentials", scope = "data:read data:write" };

                var response = _owner.httpService.PostAsync(Secrets.BASE_DOMAIN + "authentication/v2/token",
                                                            header,
                                                            data: new Dictionary<string, string>
                                                            {
                                                                ["grant_type"] = "client_credentials",
                                                                ["scope"] = "data:read data:write"
                                                            });
                dynamic data = JObject.Parse(response.Result);

                this.Value = data.access_token;

                string secondsUntilExpiration = data.expires_in;
                this.expiresAt = DateTime.Now.AddSeconds(double.Parse(secondsUntilExpiration) - 60);
            }

            public bool IsExpired()
            {
                return DateTime.Now >= this.expiresAt;
            }
        }

        public ApsContext(string projectName, string internalCloudPath) 
        {
            this.httpService = new();
            this.logHandle = new();

            // read json and populate project info if found
            ReadCache(projectName, internalCloudPath);

            // if this.project == null, this.project?.GetFolderId() evaluates to null and doesn't throw an exception
            if(this.project?.GetFolderId() == null) // cannot be null here unless big exception occurred
            {
                // project or destination folder isn't cached yet, find it and store it
                FindFolderVars(internalCloudPath);
            }


        }

        ~ApsContext() 
        {
            // release data structures
        }

        private void ReadCache(string projectName, string internalCloudPath)
        {
            try
            {
                // obtain settings path
                string config = Environment.ProcessPath?.Replace("bim-push.exe", "") + "project_info.json";

                // get projects obj
                var sample = JsonConvert.DeserializeObject(File.ReadAllText(config)) as JObject;

                // search for project name == projectName
                JObject? selectedProject = sample?["projects"]?.Values<JObject>().FirstOrDefault(proj => proj?["name"]?.Value<string>() == projectName);

                if (selectedProject != null)
                {
                    Console.WriteLine($"Found project: {selectedProject}");

                    // if found, query folder_data looking for name = internalCloudPath[len - 1]
                    JObject selectedFolder = selectedProject["folder_data"].Values<JObject>().FirstOrDefault(folder => folder["name"].Value<string>() == internalCloudPath.Split("/")[^1]);

                    if (selectedFolder != null)
                    {
                        Console.WriteLine($"Found folder: {selectedFolder}");

                        // if found initialize _project with id and objectCount
                        this.project = new(projectName, selectedFolder["id"]?.ToString(), int.Parse(selectedFolder["objectCount"]?.ToString()));
                    }
                    else
                    {
                        Console.WriteLine($"Folder not found: {selectedFolder}");

                        // store it's project id
                        this.project = new(projectName, selectedProject["id"]?.ToString());
                    }
                }
                else
                {
                    Console.WriteLine("Project not found.");
                    
                    this.project = new(projectName);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                
                // no need to hard exit, execution can continue
                this.logHandle.Log($"An unexpected error occurred: {ex.ToString}", true);
            }
        }

        private void FindFolderVars(string cloudPath)
        {
            if (this.project?.GetProjectId() != null)
            {
                // the project already exists within the cache but the folder isn't

            }
            else
            {
                // nor the project or the folder was found

            }
            
        }

        private void WriteCache(JObject folderInfo)
        {
            // by this point, ApsProject must have already been fully initialized, no checkings needed, just write it to cache
            string config = Environment.ProcessPath?.Replace("bim-push.exe", "") + "project_info.json";

            // check existance of project_info.json file
            JObject root = File.Exists(config) && new FileInfo(config).Length > 0
                ? JObject.Parse(File.ReadAllText(config)) 
                : new JObject();

            // repeated checks of existance to avoid exceptions
            if (root["hubs"] is not JArray hubs)
            {
                hubs = new JArray();
                root["hubs"] = hubs;
            }

            // search the hub id
            var hub = hubs.OfType<JObject>().FirstOrDefault(h =>
                string.Equals((string?)h["id"], this.project.GetHubId(), StringComparison.Ordinal));

            if(hub is null)
            {
                hub = new JObject
                {
                    ["id"] = this.project.GetHubId(),
                    ["projects"] = new JArray()
                };
                hubs.Add(hub);
            }

            // create projects array if it doesn't exist
            if (hub["projects"] is not JArray projects)
            {
                projects = new JArray();
                hubs["projects"] = projects;
            }

            // search the project
            var project = projects.OfType<JObject>().FirstOrDefault(p =>
                string.Equals((string?)p["id"], this.project.GetProjectId(), StringComparison.Ordinal));

            if(project is null)
            {
                project = new JObject
                {
                    ["id"] = this.project.GetProjectId(),
                    ["name"] = this.project.GetName(),
                    ["folder_data"] = new JArray()
                };
                projects.Add(project);
            }

            // create folders array if there is none
            if (project["folder_data"] is not JArray folders)
            {
                folders = new JArray();
                project["folder_data"] = folders;
            }

            // at the very least the current folder didn't exist in order for this method to be called, so no need to check
            folders.Add(folderInfo);

            var tmp = Path.ChangeExtension(config, "tmp");
            File.WriteAllText(tmp, root.ToString(Formatting.Indented));
            if (File.Exists(config))
            {
                File.Replace(tmp, config, null);
            }
            else
            {
                File.Move(tmp, config);
            }
        }

        public void Run(string serverPath)
        {
            AuthenticationToken token = new(this);
            //Console.WriteLine(token.Value);
        }


    }
}
