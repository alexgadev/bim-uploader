using Autodesk.DataManagement;
using Autodesk.DataManagement.Model;

using Autodesk.Authentication;
using Autodesk.Authentication.Model;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.SDKManager;


namespace bim_push
{
    public class ApsContext
    {
        private HttpService httpService;
        private Logger logHandle;
        private AuthenticationToken? token = null;
        private ApsProject? project = null;

        private SDKManager _SDKManager = null;

        private class ApsProject
        {
            private string name;
            private string hubId;
            private string projectId;
            private string folderId;
            private int objectCount;

            public ApsProject(string name, string? hubId = null, string? projectId = null, string? folderId = null, int? objectCount = null)
            {
                this.name = name;
                this.hubId = hubId;
                this.projectId = projectId;
                this.folderId = folderId;
                this.objectCount = objectCount == null ? -1 : (int)objectCount;
            }

            public string GetName() { return this.name; }
            public string GetHubId() { return this.hubId; }
            public void SetHubId(string id) { this.hubId = id; }
            public void SetProjectId(string id) { this.projectId = id; }
            public string GetProjectId() { return this.projectId; }
            public void SetFolderId(string id) {  this.folderId = id; }
            public string GetFolderId() {  return this.folderId; }
            public void SetObjectCount(int count) { this.objectCount = count; }
            public int GetObjectCount() { return this.objectCount; }


            public override string ToString()
            {
                return $"{this.GetName()}: {this.GetHubId()}, {this.GetProjectId()}, {this.GetFolderId()}, {this.GetObjectCount().ToString()}";
            }
        }

        private class AuthenticationToken
        {
            public string Value;
            
            private DateTime expiresAt;

            public AuthenticationToken(ApsContext owner)
            {
                AuthenticationClient authenticationClient = new AuthenticationClient(owner._SDKManager);
                dynamic token = authenticationClient.GetTwoLeggedTokenAsync(Secrets.APS_CLIENT_ID, Secrets.APS_CLIENT_SECRET, new List<Scopes>{ Scopes.DataRead, Scopes.DataWrite });

                this.Value = token.Result.AccessToken;

                this.expiresAt = DateTime.Now.ToUniversalTime().AddSeconds(token.Result.ExpiresIn);
            }

            public bool IsExpired()
            {
                return DateTime.Now.ToUniversalTime() >= this.expiresAt;
            }
        }

        public ApsContext(string projectName, string internalCloudPath) 
        {
            this.httpService = new();
            this.logHandle = new();
            _SDKManager = SdkManagerBuilder.Create().Build();

            // read json and populate project info if found
            ReadCache(projectName, internalCloudPath);

            // if this.project == null, this.project?.GetFolderId() evaluates to null and doesn't throw an exception
            //if(this.project?.GetFolderId() is null) // this.project cannot be null here unless big exception occurred
            //{
            //    // project or destination folder isn't cached yet, find it and store it
            //    FindFolderVars(internalCloudPath);
            //}
            //
            //if (this.project?.GetFolderId() is null)
            //{
            //    this.logHandle.Log("Couldn't retrieve all variables necessary to complete upload...", true);
            //    throw new Exception("Couldn't retrieve all variables necessary to complete upload...");
            //}
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
                JObject root = File.Exists(config) && new FileInfo(config).Length > 0
                ? JObject.Parse(File.ReadAllText(config))
                : new JObject();


                var match = root["hubs"]?
                    .SelectMany(hub => (hub["projects"] ?? Enumerable.Empty<JToken>())
                        .OfType<JObject>()
                        .Select(proj => new { Hub = hub, Project = proj}))
                    .FirstOrDefault(x => (string?)x.Project["name"] == projectName);

                JObject? selectedProject = match?.Project;

                if (selectedProject is not null)
                {
                    Console.WriteLine($"Found project: {selectedProject}");

                    // if found, query folder_data looking for name = internalCloudPath[len - 1]
                    JObject selectedFolder = selectedProject["folder_data"].Values<JObject>().FirstOrDefault(folder => folder["name"].Value<string>() == internalCloudPath.Split("/")[^1]);

                    if (selectedFolder != null)
                    {
                        Console.WriteLine($"Found folder: {selectedFolder}");

                        // if found initialize _project with id and objectCount
                        this.project = new ApsProject(projectName,
                                                    (string?)match.Hub["id"],
                                                    (string?)match.Project["id"],
                                                    selectedFolder["id"]?.ToString(),
                                                    int.Parse(selectedFolder["objectCount"]?.ToString()));
                    }
                    else
                    {
                        Console.WriteLine($"Folder not found: {selectedFolder}");

                        // store it's project id
                        this.project = new ApsProject(projectName,
                                                    (string?)match.Hub["id"],
                                                    (string?)match.Project["id"]);
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
            this.token = new(this);
            
            AuthenticationHeaderValue authString = new("Bearer", this.token.Value);

            HttpService.Header header = new(authString);

            Task<string> response;
            dynamic json;
            if (this.project?.GetProjectId() is null)
            {
                // nor the project or the folder was found
                response = this.httpService.GetAsync(Secrets.BASE_DOMAIN + "project/v1/hubs", header);

                json = JObject.Parse(response.Result);

                var hubs = json.data; // JArray of hub ids, we can use those to request project info inside them and check if current project is in them

                foreach (var hub in hubs) // theoretically, only two hubs  exist so no problem iterating over the array
                {
                    if(hub is not null)
                    {
                        response = this.httpService.GetAsync($"{Secrets.BASE_DOMAIN}project/v1/hubs/{hub["id"].ToString()}/projects", header);

                        json = JObject.Parse(response.Result);

                        JArray? projects = json.data;

                        var project = projects.OfType<JObject>().FirstOrDefault(p =>
                                string.Equals((string?)p?["attributes"]?["name"], this.project.GetName(), StringComparison.Ordinal));

                        if(project is not null)
                        {
                            Console.WriteLine($"{hub["id"].ToString()}, {project["id"]?.ToString()}");
                            this.project?.SetHubId(hub["id"].ToString());
                            this.project?.SetProjectId(project["id"]?.ToString());
                            break;
                        }
                    }
                }

                if(this.project.GetProjectId() is null)
                {
                    this.logHandle.Log("Project wasn't found, check correctness of the name", true);
                    return; // TODO: might want to take another look on this exit point
                }
            }

            string[] cloudArray = cloudPath.Split("/");

            // the project is already store but the folder isn't
            // find first folder ID "Project Files"
            // GET /topFolders
            response = this.httpService.GetAsync($"{Secrets.BASE_DOMAIN}project/v1/hubs/{this.project.GetHubId()}/projects/{this.project.GetProjectId()}/topFolders", header);
            
            json = JObject.Parse(response.Result);

            JArray? data = json.data;
            var topFolder = data.OfType<JObject>().FirstOrDefault(f =>
                                string.Equals((string?)f?["attributes"]?["name"], cloudArray[0], StringComparison.Ordinal));

            if(topFolder is not null)
            {
                string folderId = topFolder["id"].ToString();
                JObject? folder = null;
                // recursive find nested folders until end goal is reached
                // GET folder_id:/contents
                for(int i = 1; i < cloudArray.Length; i++)
                {
                    response = this.httpService.GetAsync($"{Secrets.BASE_DOMAIN}data/v1/projects/{this.project.GetProjectId()}/folders/{folderId}/contents", header);

                    json = JObject.Parse(response.Result);

                    data = json.data;

                    try
                    {
                        folder = data.OfType<JObject>().FirstOrDefault(d =>
                                        string.Equals((string?)d?["attributes"]?["displayName"], cloudArray[i], StringComparison.Ordinal));
                        folderId = folder?["id"]?.ToString();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.GetType().ToString());
                        this.logHandle.Log($"Folder {cloudArray[i]} couldn't be found, check correctness of the name", true);
                        return; // TODO: might want to take another look on this exit point
                    }
                }
                this.project.SetFolderId(folderId);
                this.project.SetObjectCount(int.Parse(folder["attributes"]["objectCount"].ToString()));
            }

            WriteCache(new JObject
                {
                    ["id"] = this.project.GetFolderId(),
                    ["name"] = cloudArray[^1],
                    ["objectCount"] = this.project.GetObjectCount()
                });
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

        private string? GetFileId(string filename)
        {
            int pages = (int)Math.Floor(this.project.GetObjectCount() / 200.00);

            HttpService.Header header = new(new AuthenticationHeaderValue("Bearer", this.token.Value));
            for (int i = 0; i <= pages; i++)
            {
                if (this.token.IsExpired())
                {
                    this.token = new(this);
                    header = new(new AuthenticationHeaderValue("Bearer", this.token.Value));
                }
                
                var response = httpService.GetAsync(
                    $"{Secrets.BASE_DOMAIN}data/v1/projects/{this.project.GetProjectId()}/folders/{this.project.GetFolderId()}/contents",
                    header,
                    query: new Dictionary<string, string>
                    {
                        ["page[number]"] = i.ToString()
                    });

                dynamic json = JObject.Parse(response.Result);

                JArray? contents = json.data;

                var project = contents.OfType<JObject>().FirstOrDefault(c =>
                        string.Equals((string?)c?["attributes"]?["displayName"], filename, StringComparison.Ordinal));

                if (project is not null) return project["id"].ToString();
                else Thread.Sleep(5000);
            }
            return null;
        }

        private bool WasModified(string? fileId)
        {
            if (fileId is null) return true; // file doesn't exist in the cloud yet
            return true;
        }

        public void Run(string serverPath)
        {
            IEnumerable<string> paths;
            // folder passed as starting point, upload all files under this directory and subdirectories
            if((new DirectoryInfo(serverPath).Attributes & FileAttributes.Directory) == FileAttributes.Directory)
            {
                paths = Directory
                    .EnumerateFiles(serverPath, "*", new EnumerationOptions { RecurseSubdirectories = true })
                    .Where(f => !Path.GetExtension(f).Equals(".log", StringComparison.OrdinalIgnoreCase));

            }
            else // single file upload
            {
                paths = [serverPath];
            }

            foreach(string path in paths)
            {
                string? fileId = GetFileId(path);
                if (!WasModified(fileId))
                {
                    this.logHandle.Log($"File {path} has not been modified, thus will not be updated in the cloud", true);
                }
                else
                {
                    // Step 2: Create a Storage Object
                    //object_id, object_key, bucket_key = create_object(logger=logger, access_token=access_token, folder_id=folder_id, project_id=project_id, filename=filename) 
                    //StoragePayload sp = new();
                    //Task<Storage> st = this.client.CreateStorageAsync(this.project.GetProjectId(), sp);

                    // Step 3: Generate a Signed S3 URL
                    //upload_key, urls = generate_signed_url(logger = logger, access_token = access_token, bucket_key = bucket_key, object_key = object_key)

                    // Step 4: Upload a File to the Signed URL
                    //upload_file(logger, urls, filepath)

                    // Step 5: Complete the Upload
                    //complete_upload(logger, access_token, upload_key, bucket_key, object_key)

                    // Step 6: Get the file id if it exists
                    //file_id = get_file_id(logger, access_token, project_id, folder_id, objectCount, filename)
                    //if file_id != None: # if file id is None then it means it couldn't be found so a new version must be created
                    //   Step 7a: Create Additional Versions of the File
                    //  add_additional_version(logger, access_token, filename, file_id, object_id, project_id)
                    //else:
                    //   Step 7b: Create the First Version of the File
                }
            }
        }
    }
}
