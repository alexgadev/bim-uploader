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
    /// <summary>
    ///   Represents the context for interacting with the Autodesk Platform Services (APS) API. 
    ///   It manages authentication, project and folder information, and file uploads to the cloud.
    ///   
    ///   An APS application is needed in order to use this class. The client ID and secret must be provided in the Secrets class and the application
    ///   must be provisioned with access to the necessary hubs and projects in order to upload files to the cloud. 
    ///   (The application must be a server-to-server, no human interaction needed)
    /// </summary>
    public class ApsContext
    {
        private readonly SDKManager _SDKManager; // SDKManager is a class that manages the SDK and provides access to the various clients for interacting with the APS API.
        private readonly Logger _logHandle; // Logger is a class that handles logging messages to a log file, including error messages.
        private AuthenticationToken _token;
        private readonly ApsProjectFolder _project;

        /// <summary>
        ///  Represents a project folder in the APS context. It contains information about the project, hub, folder, and object count.
        /// </summary>
        /// <param name="name">The name of the project.</param>
        /// <param name="hubId">The ID of the hub where the project is located.</param>
        /// <param name="projectId">The ID of the project.</param>
        /// <param name="folderId">The ID of the folder where the files will be uploaded.</param>
        /// <param name="objectCount">The number of objects inside the folder, used to determine how many pages of files exist in the cloud.</param>
        private class ApsProjectFolder(string name, string? hubId = null, string? projectId = null, string? folderId = null, int? objectCount = null)
        {
            private readonly string name = name;
            private string? hubId = hubId;
            private string? projectId = projectId;
            private string? folderId = folderId;
            private int? objectCount = objectCount;

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
                return $"{this.GetName()}: {this.GetHubId()}, {this.GetProjectId()}, {this.GetFolderId()}, {this.GetObjectCount()}";
            }
        }

        /// <summary>
        ///   Represents an authentication token used for accessing the APS API. It contains the token value and its expiration time.
        /// </summary>
        private class AuthenticationToken
        {
            public string Value; // value of the token used for every request to the APS API
            private readonly DateTime ExpiresAt;

            public AuthenticationToken(ApsContext owner)
            {
                AuthenticationClient authenticationClient = new(sdkManager: owner._SDKManager);
                dynamic token = authenticationClient.GetTwoLeggedTokenAsync(clientId: Secrets.APS_CLIENT_ID, clientSecret: Secrets.APS_CLIENT_SECRET, scopes: [Scopes.DataRead, Scopes.DataWrite], throwOnError: true);

                this.Value = token.Result.AccessToken;
                this.ExpiresAt = DateTime.Now.ToUniversalTime().AddSeconds(token.Result.ExpiresIn);
            }

            /// <summary>
            /// Checks whether the token has expired based on the current time and the expiration time.
            /// </summary>
            /// <returns>True if it has expired, false otherwise.</returns>
            public bool IsExpired()
            {
                return DateTime.Now.ToUniversalTime() >= this.ExpiresAt;
            }
        }

        public ApsContext(Logger logHandle, string projectName) 
        {
            _SDKManager = SdkManagerBuilder.Create().Build(); // Creates and builds the SDKManager instance,
                                                              // it also instantiates important variables like the HttpClient and the HttpClientHandler
                                                              // or the number of retries and timeout for every request to the APS API. Check the official documentation for more information about the SDKManager class.
                                                              // Used to initialize the clients for interacting with the APS API (AuthenticationClient, DataManagementClient, OssClient, etc.)
            _logHandle = logHandle;
            _token = new(owner: this);
            _project = new(name: projectName);
        }

        /// <summary>
        ///     Read cache and populate _project with hubId, projectId, folderId and objectCount if found
        /// </summary>
        /// 
        /// <param name="projectName">The name of the project to read from cache.</param>
        /// <param name="internalCloudPath">The path to the cloud internal filesystem folder.</param>
        ///     
        /// <remarks>
        ///     It will only populate the variables found inside the cache, if the project or the folder isn't found, it will leave the variables as null.
        ///     It will not throw an exception if the cache file doesn't exist or is empty, that's controlled by the caller.
        /// </remarks>
        private void ReadCache(string projectName, string internalCloudPath)
        {
            try
            {
                // obtain settings path from the executable path, and read the json file if it exists and is not empty
                string config = Environment.ProcessPath?.Replace("bim-push.exe", "") + "project_info.json";

                // read the whole json object into root
                JObject root = File.Exists(config) && new FileInfo(config).Length > 0
                ? JObject.Parse(File.ReadAllText(config)) 
                : [];

                // match for the project name inside the hubs array
                var match = root["hubs"]?
                    .SelectMany(hub => (hub["projects"] ?? Enumerable.Empty<JToken>())
                        .OfType<JObject>()
                        .Select(proj => new { Hub = hub, Project = proj}))
                    .FirstOrDefault(x => (string?)x.Project["name"] == projectName);

                JObject? selectedProject = match?.Project; // this could be null if the project isn't found, but the hub is, or if neither is found

                if (selectedProject is not null) // won't bother for an else path, the caller should handle the null variables with FindFolderVarsAsync if needed
                {
                    // if found, match folder_data for name = internalCloudPath[len - 1], which is the goal folder name
                    JObject? selectedFolder = selectedProject["folder_data"]?.Values<JObject>().FirstOrDefault(folder => folder?["name"]?.Value<string>() == internalCloudPath.Split("/")[^1]);

#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning disable CS8604 // Possible null reference argument.
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                    if (selectedFolder is not null) // none of the warnings can actually happen inside the if/else sections, that's why I disabled them, the null checks are done before the dereference 
                    {
                        // initialize _project with the folder id and objectCount
                        _project.SetHubId(id: (string)match.Hub["id"]);
                        _project.SetProjectId(id: (string)match.Project["id"]);
                        _project.SetFolderId(id: selectedFolder["id"].ToString());
                        _project.SetObjectCount(count: int.Parse(selectedFolder["objectCount"].ToString()));
                    }
                    else
                    {
                        // store it's project and hub id
                        _project.SetHubId(id: (string)match.Hub["id"]);
                        _project.SetProjectId(id: (string)match.Project["id"]);
                    }
#pragma warning restore CS8602 // Dereference of a possibly null reference.
#pragma warning restore CS8604 // Possible null reference argument.
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
                }
            }
            catch (Exception ex)
            {
                // no need to hard exit, execution can continue, caller should handle it
                _logHandle.Log(message: $"An unexpected error occurred when attempting to read from cache: {ex.ToString}", error: true);
            }
        }

        /// <summary>
        ///    Find the hubId, projectId, folderId and objectCount for the given project and cloudPath, and store them in _project. 
        /// </summary>
        /// 
        /// <param name="internalCloudPath">The path to the cloud internal filesystem folder.</param>
        /// 
        /// <remarks>
        ///     Execution of this function doesn't ensure that the variables will be found and properly populated, it is an attempt at doing so.
        ///     If the project or the folder isn't found, the variables will be left as null and the caller should handle that case.
        /// </remarks>
        private async Task FindFolderVarsAsync(string internalCloudPath)
        {
            DataManagementClient client = new(sdkManager: _SDKManager); 

            if (_project.GetProjectId() is null) // different entry points, this function might be called with nothing populated or only part of it
            {
                // nor the project or the folder was found
                if (_token.IsExpired())
                {
                    _token = new(owner: this);
                } // GetHubsAsync returns an object with ALL hubs the app provisioned has access to, in our case there should only be two, EU(ACC) and EEUU(BIM360)
                var hubs = await client.GetHubsAsync(accessToken: _token.Value, throwOnError: true);
                var hubsData = hubs.Data;

                foreach (var hub in hubsData)
                {
                    if(hub is not null)
                    {
                        if (_token.IsExpired())
                        {
                            _token = new(owner: this);
                        } // same as GetHubsAsync, GetHubProjectsAsync returns an object with ALL projects the app provisioned has access to inside each hub
                        var projects = await client.GetHubProjectsAsync(hubId: hub.Id, accessToken: _token.Value, throwOnError: true);
                        var project = projects.Data.FirstOrDefault(p =>
                                string.Equals(p.Attributes.Name, _project.GetName(), StringComparison.Ordinal));
                        
                        if(project is not null)
                        {
                            _project.SetHubId(id: hub.Id);
                            _project.SetProjectId(id: project.Id);
                            break;
                        }
                    }
                }
            }
            // if by this point the project hasn't been found there's no actual way to continue execution to find the rest of the variables
            if(_project.GetProjectId() is null)
            {
                _logHandle.Log(message: "Project wasn't found, check correctness of the name", error: true);
            }
            else
            {
                string[] cloudArray = internalCloudPath.Split("/");

                if (_token.IsExpired())
                {
                    _token = new(owner: this);
                } // find first folder ID "Project Files"
                var topfolders = await client.GetProjectTopFoldersAsync(hubId: _project.GetHubId(), projectId: _project.GetProjectId(), accessToken: _token.Value, throwOnError: true);
                var topfolder = topfolders.Data.FirstOrDefault(f =>
                                    string.Equals(f.Attributes.Name, cloudArray[0], StringComparison.Ordinal));

                if (topfolder is not null)
                {
                    string folderId = topfolder.Id;

                    // recursive find nested folders until end goal is reached
                    dynamic? folder = null;
                    for(int i = 1; i < cloudArray.Length; i++)
                    {
                        if (_token.IsExpired())
                        {
                            _token = new(owner: this);
                        }
                        var folderContent = await client.GetFolderContentsAsync(projectId: _project.GetProjectId(), folderId: folderId, accessToken: _token.Value, throwOnError: true);
                        try
                        {
                            folder = folderContent.Data.FirstOrDefault(d =>
                                                        string.Equals(((dynamic)d).Attributes.Name, cloudArray[i], StringComparison.Ordinal));
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                            folderId = folder.Id; // intentionally left as is so it throws an exception if the folder isn't found
#pragma warning restore CS8602 // Dereference of a possibly null reference.
                        }
                        catch (Exception)
                        {
                            folder = null;
                            _logHandle.Log(message: $"Folder {cloudArray[i]} couldn't be found, check correctness of the name", error: true);
                            break;
                        }
                    }

                    if(folder is not null)
                    {
                        _project.SetFolderId(id: folderId);
                        _project.SetObjectCount(count: int.Parse(folder.Attributes.ObjectCount.ToString()));
                        WriteCache(new JObject 
                            {
                                ["id"] = _project.GetFolderId(),
                                ["name"] = cloudArray[^1],
                                ["objectCount"] = _project.GetObjectCount()
                            });
                        _logHandle.Log(message: "New project/folder information has been cached for future use", error: false);
                    }
                    else 
                    {
                        _logHandle.Log(message: $"Could not retrieve all the information necessary related to the path {internalCloudPath}", error: true);
                    }
                }
            }
        }

        /// <summary>
        ///   Write the project and folder information to the cache file project_info.json. If the file doesn't exist, it will be created.
        /// </summary>
        /// 
        /// <param name="folderInfo">A JSON object containing the folder information to be cached.</param>
        private void WriteCache(JObject folderInfo)
        {
            // by this point, ApsProjectFolder must have already been fully initialized, no checkings needed, just write it to cache
            string config = Environment.ProcessPath?.Replace("bim-push.exe", "") + "project_info.json";

            // check existance of project_info.json file
            JObject root = File.Exists(config) && new FileInfo(config).Length > 0
                ? JObject.Parse(File.ReadAllText(config)) 
                : [];

            // repeated checks of existance to avoid exceptions
            if (root["hubs"] is not JArray hubs)
            {
                hubs = [];
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
                projects = [];
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
                folders = [];
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

        /// <summary>
        ///   Finds the id of a file in the cloud given its filename. If the file doesn't exist, it returns null.
        /// </summary>
        /// 
        /// <param name="filename">The name of the file to find.</param>
        /// <returns>The id of the file if found, otherwise null.</returns>
        private async Task<string?> GetFileId(string filename)
        {
            DataManagementClient client = new(sdkManager: _SDKManager);
            
            // max elements per page are 200
            int pages = (int)Math.Floor((double)(_project.GetObjectCount()! / 200.00));
            for (int i = 0; i <= pages; i++)
            {
                if (_token.IsExpired())
                {
                    _token = new(owner: this);
                }
                var folderContent = await client.GetFolderContentsAsync(projectId: _project.GetProjectId(), folderId: _project.GetFolderId(), pageNumber: i, accessToken: _token.Value, throwOnError: true);

                dynamic? file = folderContent.Data.FirstOrDefault(c =>
                        string.Equals(((dynamic)c).Attributes.DisplayName, filename, StringComparison.Ordinal));
                if (file is not null) return file.Id;
            }
            return null;
        }

        /// <summary>
        ///  Checks if a file has been modified since the last upload to the cloud by comparing its last write time with the last modified time of the file in the cloud.
        /// </summary>
        /// 
        /// <param name="fileId">The ID of the file in the cloud.</param>
        /// <param name="filepath">The local path of the file.</param>
        /// 
        /// <returns>true if the file has been modified, false otherwise.</returns>
        private async Task<bool> WasModified(string? fileId, string filepath)
        {
            if (fileId is null) return true; // file doesn't exist in the cloud yet
            
            DataManagementClient client = new(sdkManager: _SDKManager);

            if (_token.IsExpired())
            {
                _token = new(owner: this);
            }
            Item item = await client.GetItemAsync(projectId: _project.GetProjectId(), itemId: fileId, accessToken: _token.Value, throwOnError: true);

            return File.GetLastWriteTimeUtc(filepath) > item.Data.Attributes.LastModifiedTime.ToUniversalTime();
        }

        /// <summary>
        ///   Creates a storage object in the cloud for the given filename and returns its id, objectKey and bucketKey.
        /// </summary>
        /// 
        /// <param name="filename">Name of the file to create the storage for.</param>
        /// <returns>A list containing the storage ID, object key, and bucket key.</returns>
        private async Task<List<string>> CreateStorageObject(string filename)
        {
            DataManagementClient client = new(sdkManager: _SDKManager);
            
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
            
            if (_token.IsExpired())
            {
                _token = new(owner: this);
            }
            Storage st = await client.CreateStorageAsync(projectId: _project.GetProjectId(), storagePayload: storagePayload, accessToken: _token.Value, throwOnError: true);
            
            return [st.Data.Id, st.Data.Id.Split("/")[^1], st.Data.Id.Split("/")[^2].Split(":")[^1]];
        }

        /// <summary>
        ///  Creates the first version of a file in the cloud given its filename and storage object ID.
        /// </summary>
        /// 
        /// <param name="filename">Name of the file to create the first version for.</param>
        /// <param name="objectId">The ID of the storage object.</param>
        private async Task CreateFirstVersion(string filename, string objectId)
        {
            DataManagementClient client = new(sdkManager: _SDKManager);
            
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

            if (_token.IsExpired())
            {
                _token = new(owner: this);
            }
            await client.CreateItemAsync(projectId: _project.GetProjectId(), itemPayload: payload, accessToken: _token.Value, throwOnError: true);
        }

        /// <summary>
        ///   Creates an additional version of a file in the cloud given its filename, file ID, and storage object ID.
        /// </summary>
        /// 
        /// <param name="filename">Name of the file to create an additional version for.</param>
        /// <param name="fileId">The ID of the file.</param>
        /// <param name="objectId">The ID of the storage object.</param>
        private async Task CreateAdditionalVersion(string filename, string fileId, string objectId)
        {
            DataManagementClient client = new(sdkManager: _SDKManager);

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

            if (_token.IsExpired())
            {
                _token = new(owner: this);
            }
            await client.CreateVersionAsync(projectId: _project.GetProjectId(), versionPayload: payload, accessToken: _token.Value, throwOnError: true);
        }

        /// <summary>
        ///  Uploads files from the server path to the cloud internal path. If the server path is a directory, 
        ///  it will upload all files under that directory and its subdirectories, excluding .log files. 
        ///  If the server path is a file, it will upload that single file.
        /// </summary>
        /// 
        /// <param name="serverPath">The path to the file or directory on the server.</param>
        /// <param name="internalCloudPath">The path in the cloud where the files will be uploaded.</param>
        /// <exception cref="Exception">Thrown when the variables needed to complete the upload couldn't be found.</exception>
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
            ReadCache(projectName: _project.GetName(), internalCloudPath: internalCloudPath);

            if (_project.GetFolderId() is null)
            {
                // project or destination folder isn't cached yet, find it and store it
                await FindFolderVarsAsync(internalCloudPath: internalCloudPath);
            }

            if (_project.GetFolderId() is null) // by this point, if the folderId is still null, it means that the project or the folder isn't found in the cloud
            {
                _logHandle.Log(message: "Couldn't retrieve all variables necessary to complete upload...", error: true);
                throw new Exception(message: "Couldn't retrieve all variables necessary to complete upload...");
            }

            foreach (string path in paths)
            {
                string? fileId = GetFileId(filename: path.Split("\\")[^1]).Result;
                if (!WasModified(fileId: fileId, filepath: path).Result)
                {
                    _logHandle.Log(message: $"File {path} has not been modified, thus will not be updated in the cloud", error: true);
                }
                else
                {
                    DataManagementClient client = new(sdkManager: _SDKManager);

                    // Step 1: Create a Storage Object
                    List<string> storageInfo = await CreateStorageObject(filename: path.Split("\\")[^1]);
                    string objectId = storageInfo[0];
                    string objectKey = storageInfo[1];
                    string bucketKey = storageInfo[2];

                    // Step 2: Upload a File to a Signed URL
                    OssClient ossClient = new(sdkManager: _SDKManager);
                    if (_token.IsExpired())
                    {
                        _token = new(owner: this);
                    } // UploadObjectAsync also handles the creation of the signed URL, so we don't need to do it manually
                    await ossClient.UploadObjectAsync(bucketKey: bucketKey, objectKey: objectKey, sourceToUpload: File.OpenRead(path), accessToken: _token.Value);

                    // Step 3: Create a Version of the File in the Cloud
                    if (fileId is null)
                    {
                        await CreateFirstVersion(filename: path.Split("\\")[^1], objectId: objectId);
                    }
                    else
                    {
                        await CreateAdditionalVersion(filename: path.Split("\\")[^1], fileId: fileId, objectId: objectId);
                    }
                }
            }
        }
    }
}
