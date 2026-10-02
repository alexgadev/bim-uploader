using Autodesk.Authentication;
using Autodesk.Authentication.Model;

using Autodesk.DataManagement;
using Autodesk.DataManagement.Model;
using Autodesk.Oss;
using Autodesk.Oss.Model;
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
            _SDKManager = SdkManagerBuilder.Create().Build();
            _logHandle = new();
            _token = new(this);
            _project = new(projectName);
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
                if (_token.IsExpired())
                {
                    _token = new(this);
                }
                var hubs = await client.GetHubsAsync(accessToken: _token.Value, throwOnError: true);
                var hubsData = hubs.Data;

                foreach (var hub in hubsData) // theoretically, only two hubs exist so no problem iterating over the array
                {
                    if(hub is not null)
                    {
                        if (_token.IsExpired())
                        {
                            _token = new(this);
                        }
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
                if (_token.IsExpired())
                {
                    _token = new(this);
                }
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
                        if (_token.IsExpired())
                        {
                            _token = new(this);
                        }
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

        private async Task<bool> WasModified(string? fileId, string filepath)
        {
            if (fileId is null) return true; // file doesn't exist in the cloud yet
            
            DataManagementClient client = new(_SDKManager);
            Item item = await client.GetItemAsync(_project.GetProjectId(), fileId, _token.Value, throwOnError: true);

            // "lastModifiedTime": "2025-01-29T10:08:06.0000000Z"
            return File.GetLastWriteTimeUtc(filepath) <= item.Data.Attributes.LastModifiedTime.ToUniversalTime();
        }

        private async Task<List<string>> CreateStorageObject(string filename)
        {
            DataManagementClient client = new(_SDKManager);

            StoragePayload storagePayload = new() { 
                Jsonapi = new JsonApiVersion() { 
                    VarVersion = JsonApiVersionValue._10 
                }, 
                Data = new() { 
                    Type = TypeObject.Objects, 
                    Attributes = new() { 
                        Name = filename 
                    }, 
                    Relationships = new() { 
                        Target = new() { 
                            Data = new() { 
                                Type = TypeFolderItemsForStorage.Folders, 
                                Id = _project.GetFolderId() 
                            }
                        }
                    }
                }
            };

            Storage st = await client.CreateStorageAsync(_project.GetProjectId(), storagePayload, _token.Value, throwOnError: true);
            
            return [st.Data.Id, st.Data.Id.Split("/")[^1], st.Data.Id.Split("/")[^2].Split(":")[^1]];
        }

        private async Task CreateFirstVersion(string filename, string objectId)
        {
            DataManagementClient client = new(_SDKManager);
            
            ItemPayload payload = new() { 
                Jsonapi = new JsonApiVersion() { VarVersion = JsonApiVersionValue._10 }, 
                Data = new() { 
                    Type = TypeItem.Items, 
                    Attributes = new() { 
                        DisplayName = filename, 
                        Extension = new() { 
                            Type = "items:autodesk.bim360:File", 
                            VarVersion = "1.0" 
                        }
                    }, 
                    Relationships = new() { 
                        Tip = new() { 
                            Data = new() { 
                                Type = TypeVersion.Versions, 
                                Id = "1" 
                            } 
                        }, 
                        Parent = new() { 
                            Data = new() { 
                                Type = TypeFolder.Folders, 
                                Id = _project.GetFolderId() 
                            } 
                        }
                    }
                }, 
                Included = [ new() {
                    Type = TypeVersion.Versions,
                    Id = "1",
                    Attributes = new() {
                        Name = filename,
                        Extension = new() {
                            Type = "versions:autodesk.bim360:File",
                            VarVersion = "1.0"
                        }
                    },
                    Relationships = new() {
                        Storage = new() {
                            Data = new() {
                                Type = TypeObject.Objects,
                                Id = objectId
                            }
                        }
                    } 
                }]
            };

            await client.CreateItemAsync(_project.GetProjectId(), payload, _token.Value, throwOnError: true);
        }

        private async Task CreateAdditionalVersion(string filename, string fileId, string objectId)
        {
            DataManagementClient client = new(_SDKManager);

            VersionPayload payload = new()
            {
                Jsonapi = new JsonApiVersion() { VarVersion = JsonApiVersionValue._10 },
                Data = new()
                {
                    Type = TypeVersion.Versions,
                    Attributes = new()
                    {
                        Name = filename,
                        Extension = new()
                        {
                            Type = "versions:autodesk.bim360:File",
                            VarVersion = "1.0"
                        }
                    },
                    Relationships = new()
                    {
                        Item = new()
                        {
                            Data = new()
                            {
                                Type = TypeItem.Items,
                                Id = fileId
                            }
                        },
                        Storage = new()
                        {
                            Data = new()
                            {
                                Type = TypeObject.Objects,
                                Id = objectId
                            }
                        }
                    }
                }
            };

            await client.CreateVersionAsync(_project.GetProjectId(), payload, _token.Value, throwOnError: true);
        }

        public async Task UploadAsync(string serverPath, string internalCloudPath)
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
                if (!WasModified(fileId, path).Result)
                {
                    _logHandle.Log($"File {path} has not been modified, thus will not be updated in the cloud", true);
                }
                else
                {
                    DataManagementClient client = new(_SDKManager);

                    // Step 1: Create a Storage Object
                    List<string> storageInfo = await CreateStorageObject(path.Split("/")[^1]);
                    string objectId = storageInfo[0];
                    string objectKey = storageInfo[1];
                    string bucketKey = storageInfo[2];

                    // Step 2: Upload a File to a Signed URL
                    OssClient ossClient = new(_SDKManager);
                    await ossClient.UploadObjectAsync(bucketKey: bucketKey, objectKey: objectKey, sourceToUpload: File.OpenRead(path), accessToken: _token.Value);

                    // Step 3: Create a Version of the File in the Cloud
                    if (fileId is null)
                    {
                        await CreateFirstVersion(path.Split("/")[^1], objectId);
                    }
                    else
                    {
                        await CreateAdditionalVersion(path.Split("/")[^1], fileId, objectId);
                    }
                }
            }
        }
    }
}
