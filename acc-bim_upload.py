import json
import sys
from os import path, walk
from create_env import get_vars
import request_methods 
from logger import Logger

# Into the thick of it
if __name__ == "__main__":
   logger = Logger()

   # Check first if API is up and responsive
   if not request_methods.api_up():
      logger.log("API isn't accessible", True)
      exit(-1) 

   # Initially assume execution will be for just one file
   recurse = False

   # Get an access token with which to make the authenticated requests against the RESTful API
   try:
      access_token = request_methods.get_two_legged_token(logger)
   except request_methods.APIException:
      exit(-1)

   # Check number of arguments given, less than 4 or more than 5 is not a supported behaviour
   # Order of arguments is as follows: 
   # <path to "python.exe"> <path to this script> <name of project> <path to the server> <inside path in cloud> <[optional] recursion>
   if len(sys.argv) == 4 or len(sys.argv) == 5:
      project = sys.argv[1]
      server_path = sys.argv[2]
      cloud_path = sys.argv[3]
      if len(sys.argv) == 5:
         if sys.argv[4] == "true" or sys.argv[4] == "True":
            recurse = True
   else:
      exit(1)
   
   # Transform the arguments obtained as needed
   cloud_array = cloud_path.split("/") # use the internal cloud path to create the name and path of the environment variables json file
   env_path = __file__.replace("acc-bim_upload.py", "settings.json")
   
   # Using path from os, check if the environment variables json exists
   project_id = folder_id = objectCount = None
   if path.isfile(env_path):
      with open(env_path, "r") as outfile:
         obj = json.load(outfile)
         # check if at least a project exists within settings.json
         project_info = list(filter(lambda x:x["name"] == project, obj["projects"])) #TODO: re-examine this idk why i filtered and then forgot about it to search it through a for loop either way lol
         if len(project_info) != 0: # len > 0 means the project exists within settings.json
            for i, proj in enumerate(obj["projects"]): # iterate through projects
               if proj["name"] == project:
                  project_id = proj["id"]
                  for j, folder in enumerate(proj["folder_data"]):
                     if folder["name"] == cloud_array[len(cloud_array) - 1]:
                        folder_id = folder["id"]
                        objectCount = folder["objectCount"]
                        break
   if project_id == None or folder_id == None or objectCount == None:
      project_id, folder_id, objectCount = get_vars(logger, access_token, project, cloud_path)

   # When server_path given is a directory recurse through every file and directory below it
   if not recurse:
      server_array = server_path.split("\\")
      filename = server_array[len(server_array) - 1] # obtain the filename (if path given is a single file)

      if not request_methods.upload_request_flow(logger=logger,
                        access_token=access_token, 
                         project_id=project_id, 
                         folder_id=folder_id, 
                         objectCount=objectCount,
                         filepath=server_path, 
                         filename=filename):
         logger.log("Failed to upload file " + filename)
      else:
         logger.log("Successfully completed upload of file " + filename)
   else:
      # check if server path given is a directory that exists
      # loop through directories
      for (dirpath, dirnames, filenames) in walk(server_path):
         for filename in filenames:
            extension = filename[len(filename) - 4:]
            if extension != ".log":

               if not request_methods.upload_request_flow(logger=logger,
                              access_token=access_token, 
                               project_id=project_id, 
                               folder_id=folder_id, 
                               objectCount=objectCount,
                               filepath=dirpath + "\\" + filename, 
                               filename=filename):
                  logger.log("Failed to upload file " + filename)
               else:
                  logger.log("Successfully completed upload of file " + filename)
   logger.destroy()