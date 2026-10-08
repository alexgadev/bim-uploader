using bim_push;

/*
 * bim-push - .NET 8 CLI
 * 
 * Usage:
 *      bim-push.exe <projectName> <serverPath> <internalCloudPath>
 *      
 *      projectName         - Name of the project that appears in the cloud.
 *                            It must be exactly the same as the one that appears 
 *                            when selecting a project.
 *      
 *      serverPath          - Path where the file or files to be uploaded are
 *                            found inside a server.
 *      
 *      internalCloudPath   - the sequence of folders accesses inside the cloud 
 *                            project in order to find the goal directory where
 *                            the file(s) will be uploaded.
 */


if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: bim-push.exe <projectName> <serverPath> <internalCloudPath>");
    return 1;
}

string projectName = args[0];
string serverPath = args[1];
string internalCloudPath = args[2];

Logger logHandle = new(projectName);
logHandle.Log(message: $"Execution started for server path: {serverPath}, internal cloud path: {internalCloudPath}");

if((new DirectoryInfo(serverPath).Attributes & FileAttributes.Directory) != FileAttributes.Directory && !File.Exists(serverPath))
{
    logHandle.Log(message: $"Server path is invalid: {serverPath}", error: true);
    return 1;
}

try
{
    ApsContext apsContext = new(logHandle: logHandle, projectName: projectName);
    await apsContext.UploadAsync(serverPath: serverPath, internalCloudPath: internalCloudPath);

    Console.WriteLine("Workflow completed.");
    logHandle.Dispose();
    return 0;
}
catch(Exception ex)
{
    Console.Error.WriteLine($"Fatal error: {ex.Message}");
    Console.Error.WriteLine(ex.StackTrace);

    logHandle.Log(message: $"Execution ended due to error: {ex.Message}", error: true);
    logHandle.Dispose();
    return 2;
}