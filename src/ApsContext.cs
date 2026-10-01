using Autodesk.Authentication;
using Autodesk.Authentication.Model;

using Autodesk.DataManagement;
using Autodesk.DataManagement.Model;

using Autodesk.SDKManager;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;


namespace bim_push
{
    public class ApsContext
    {
        private readonly SDKManager _SDKManager;
        private readonly Logger _logHandle;
        private AuthenticationToken _token;
        private readonly ApsProjectFolder _project;

        private class ApsProjectFolder
        {
            private string name;
            private string? hubId;
            private string? projectId;
            private string? folderId;
            private int? objectCount;

            public ApsProjectFolder(string name, string? hubId = null, string? projectId = null, string? folderId = null, int? objectCount = null)
            {
                this.name = name;
                this.hubId = hubId;
                this.projectId = projectId;
                this.folderId = folderId;
                this.objectCount = objectCount;
            }

            public string GetName() { return this.name; }
            public string? GetHubId() { return this.hubId; }
            public void SetHubId(string id) { this.hubId = id; }
            public void SetProjectId(string id) { this.projectId = id; }
            public string? GetProjectId() { return this.projectId; }
            public void SetFolderId(string id) {  this.folderId = id; }
            public string? GetFolderId() {  return this.folderId; }
            public void SetObjectCount(int count) { this.objectCount = count; }
            public int? GetObjectCount() { return this.objectCount; }


            public override string ToString()
            {
                return $"{this.GetName()}: {this.GetHubId()}, {this.GetProjectId()}, {this.GetFolderId()}, {this.GetObjectCount().ToString()}";
            }
        }

        private class AuthenticationToken
        {
            public string Value;
            private readonly DateTime ExpiresAt;

            public AuthenticationToken(ApsContext owner)
            {
                AuthenticationClient authenticationClient = new(owner._SDKManager);
                dynamic token = authenticationClient.GetTwoLeggedTokenAsync(Secrets.APS_CLIENT_ID, Secrets.APS_CLIENT_SECRET, [Scopes.DataRead, Scopes.DataWrite], throwOnError: true);

                this.Value = token.Result.AccessToken;
                this.ExpiresAt = DateTime.Now.ToUniversalTime().AddSeconds(token.Result.ExpiresIn);
            }

            public bool IsExpired()
            {
                return DateTime.Now.ToUniversalTime() >= this.ExpiresAt;
            }
        }

        public ApsContext(string projectName) 
        {
            _logHandle = new();
            _SDKManager = SdkManagerBuilder.Create().Build();
            _project = new(projectName);
            _token = new(this);
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
                : [];


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
                    JObject? selectedFolder = selectedProject["folder_data"]?.Values<JObject>().FirstOrDefault(folder => folder?["name"]?.Value<string>() == internalCloudPath.Split("/")[^1]);

                    if (selectedFolder is not null)
                    {
                        Console.WriteLine($"Found folder: {selectedFolder}");

                        // if found initialize _project with id and objectCount
                        _project.SetHubId((string)match.Hub["id"]);
                        _project.SetProjectId((string)match.Project["id"]);
                        _project.SetFolderId(selectedFolder["id"].ToString());
                        _project.SetObjectCount(int.Parse(selectedFolder["objectCount"].ToString()));
                    }
                    else
                    {
                        Console.WriteLine($"Folder not found: {selectedFolder}");

                        // store it's project id
                        _project.SetHubId((string)match.Hub["id"]);
                        _project.SetProjectId((string)match.Project["id"]);
                    }
                }
                else
                {
                    // no hub where project with name == projectName exists
                    _logHandle.Log($"No hub where project with name {projectName} exists. Check correctness of the project name specified as argument.", true);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                
                // no need to hard exit, execution can continue
                _logHandle.Log($"An unexpected error occurred: {ex.ToString}", true);
            }
        }

        private async Task FindFolderVarsAsync(string cloudPath)
        {
            DataManagementClient client = new(_SDKManager);

            if (_project.GetProjectId() is null)
            {
                // nor the project or the folder was found
                var hubs = await client.GetHubsAsync(accessToken: _token.Value, throwOnError: true);
                var hubsData = hubs.Data;

                foreach (var hub in hubsData) // theoretically, only two hubs exist so no problem iterating over the array
                {
                    if(hub is not null)
                    {
                        var projects = await client.GetHubProjectsAsync(hubId: hub.Id, accessToken: _token.Value, throwOnError: true);
                        var project = projects.Data.FirstOrDefault(p =>
                                string.Equals(p.Attributes.Name, _project.GetName(), StringComparison.Ordinal));
                        
                        if(project is not null)
                        {
                            _project.SetHubId(hub.Id);
                            _project.SetProjectId(project.Id);
                            break;
                        }
                    }
                }
            }

            if(_project.GetProjectId() is null)
            {
                _logHandle.Log("Project wasn't found, check correctness of the name", true);
            }
            else
            {
                string[] cloudArray = cloudPath.Split("/");

                // the project is already store but the folder isn't, find first folder ID "Project Files"
                // GET /topFolders
                var topfolders = await client.GetProjectTopFoldersAsync(hubId: _project.GetHubId(), projectId: _project.GetProjectId(), accessToken: _token.Value, throwOnError: true);
                var topfolder = topfolders.Data.FirstOrDefault(f =>
                                    string.Equals(f.Attributes.Name, cloudArray[0], StringComparison.Ordinal));

                if (topfolder is not null)
                {
                    string folderId = topfolder.Id;

                    // recursive find nested folders until end goal is reached
                    // GET folder_id:/contents
                    dynamic? folder = null;
                    for(int i = 1; i < cloudArray.Length; i++)
                    {
                        var folderContent = await client.GetFolderContentsAsync(projectId: _project.GetProjectId(), folderId: folderId, accessToken: _token.Value, throwOnError: true);
                        try
                        {
                            folder = folderContent.Data.FirstOrDefault(d =>
                                                        string.Equals(((dynamic)d).Attributes.Name, cloudArray[i], StringComparison.Ordinal));
                            folderId = folder.Id;
                        }
                        catch (Exception ex)
                        {
                            folder = null;
                            Console.WriteLine(ex.GetType().ToString());
                            this._logHandle.Log($"Folder {cloudArray[i]} couldn't be found, check correctness of the name", true);
                        }
                    }

                    if(folder is not null)
                    {
                        _project.SetFolderId(folderId);
                        _project.SetObjectCount(int.Parse(folder.Attributes.ObjectCount.ToString()));
                        Console.WriteLine(_project);
                        WriteCache(new JObject
                            {
                                ["id"] = _project.GetFolderId(),
                                ["name"] = cloudArray[^1],
                                ["objectCount"] = _project.GetObjectCount()
                            });
                    }
                }
            }
        }

        private void WriteCache(JObject folderInfo)
        {
            // by this point, ApsProject must have already been fully initialized, no checkings needed, just write it to cache
            string config = Environment.ProcessPath?.Replace("bim-push.exe", "") + "project_info.json";

            // check existance of project_info.json file
            JObject root = File.Exists(config) && new FileInfo(config).Length > 0
                ? JObject.Parse(File.ReadAllText(config)) 
                : [];

            // repeated checks of existance to avoid exceptions
            if (root["hubs"] is not JArray hubs)
            {
                hubs = new JArray();
                root["hubs"] = hubs;
            }

            // search the hub id
            var hub = hubs.OfType<JObject>().FirstOrDefault(h =>
                string.Equals((string?)h["id"], _project.GetHubId(), StringComparison.Ordinal));

            if(hub is null)
            {
                hub = new JObject
                {
                    ["id"] = _project.GetHubId(),
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
                string.Equals((string?)p["id"], _project.GetProjectId(), StringComparison.Ordinal));

            if(project is null)
            {
                project = new JObject
                {
                    ["id"] = _project.GetProjectId(),
                    ["name"] = _project.GetName(),
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

        private async Task<string?> GetFileId(string filename)
        {
            DataManagementClient client = new(_SDKManager);
            
            int pages = (int)Math.Floor((double)(_project.GetObjectCount()! / 200.00));
            for (int i = 0; i <= pages; i++)
            {
                if (_token.IsExpired())
                {
                    _token = new(this);
                }

                var folderContent = await client.GetFolderContentsAsync(projectId: _project.GetProjectId(), folderId: _project.GetFolderId(), pageNumber: i, accessToken: _token.Value, throwOnError: true);

                dynamic? file = folderContent.Data.FirstOrDefault(c =>
                        string.Equals(((dynamic)c).Attributes.DisplayName, filename, StringComparison.Ordinal));

                if (file is not null) return file.Id;
            }
            return null;
        }

        private bool WasModified(string? fileId)
        {
            if (fileId is null) return true; // file doesn't exist in the cloud yet
            return true;
        }

        public async Task RunAsync(string serverPath, string internalCloudPath)
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

            // read json and populate project info if found
            ReadCache(_project.GetName(), internalCloudPath);

            if (_project.GetFolderId() is null)
            {
                // project or destination folder isn't cached yet, find it and store it
                await FindFolderVarsAsync(internalCloudPath);
            }

            if (_project.GetFolderId() is null)
            {
                _logHandle.Log("Couldn't retrieve all variables necessary to complete upload...", true);
                throw new Exception("Couldn't retrieve all variables necessary to complete upload...");
            }

            foreach (string path in paths)
            {
                string? fileId = GetFileId(path).Result;
                if (!WasModified(fileId))
                {
                    _logHandle.Log($"File {path} has not been modified, thus will not be updated in the cloud", true);
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
