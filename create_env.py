import requests
import json
from os import path
from const import BASE_DOMAIN
from logger import Logger

# Call GET hubs to retrieve all hubs contained within an ACC/BIM 360 account
# Successful call returns "200 OK"
def get_hubs(logger: Logger, access_token):
    headers = {"Authorization": "Bearer " + access_token}
    response = requests.get(url=BASE_DOMAIN + "project/v1/hubs", headers=headers)

    # check for successful response
    if response.status_code == 200:
        logger.log("Successful retrieval of the hubs collection")
        
        res_json = response.json()
        try:
            hubs = res_json['data']
            return hubs
        except KeyError:
            logger.log("Retrieval of the hubs collection succeeded but returned empty json object", True)
            return None
    else:
        logger.log("Retrieval of the hubs collection failed...", True)
        if response.status_code == 401:
            logger.log("The supplied Authorization header was not valid or the supplied token scope was not acceptable", True)
        else:
            logger.log("The request was successfully validated but permission is not grantedd or the application has not been white-listed", True)
        return None
    

# Call GET hubs/:hub_id/projects to get a list of all the projects in a hub, then filter by name of project
# Successful call returns "200 OK"
def get_proj_id(logger: Logger, access_token, hub_id, project):
    headers = { 'Authorization': 'Bearer ' + access_token }
    response = requests.get(url=BASE_DOMAIN + "project/v1/hubs/" + hub_id + "/projects", headers=headers)

    # check for successful response
    if response.status_code == 200:
        logger.log("Successful retrieval of the projects collection associated with the hub")

        projs = list(filter(lambda x:x["attributes"]["name"]==project, response.json()['data']))
        if len(projs) == 0:
            return None

        try:
            # ACC and BIM360 are located in different positions of the array obtained
            if len(projs[0]) == 0:
                proj_id = projs[1]['id']
            else:
                proj_id = projs[0]['id']
            logger.log("Project " + project + " was found")
            return proj_id
        except KeyError:
            logger.log("Project json object returned empty, not able to retrieve project id", True)
            return None
    else:
        logger.log("Retrieval of projects failed...")
        if response.status_code == 400:
            logger.log("The request could not be understood by the server due to malformed syntax or missing request headers", True)
        elif response.status_code == 401:
            logger.log("The supplied Authorization header was not valid or the supplied token scope was not acceptable", True)
        elif response.status_code == 402:
            logger.log("The request was successfully validated but permission is not granted or the application has not been white-listed", True)
        else:
            logger.log("The specified resource was not found", True)
        return None


# Helper method that facilitates the worflow to dinamically obtain the hub where a project is located as well as the project id and nested folder
# to which make the upload
def get_vars(logger: Logger, access_token, project, cloud_path):
    proj_id = None
    cloud_array = cloud_path.split("/")

    # Create the name of the file where the settings for each project are to be located and, in later executions, found
    file = __file__.replace("create_env.py", '') + "settings.json"
   
    # 1: Find the project ID retrieving each HUB in the account and searching where the project is located
    for hub in get_hubs(logger, access_token):
        if hub != None:
            proj_id = get_proj_id(logger, access_token, hub["id"], project)
            if proj_id != None:
                hub_id = hub["id"]
                break
    if proj_id == None:
        logger.log("Failed to find the project id for current project", True)
        return None, None, None

    # 2: Find first folder ID
    headers = { "Authorization": "Bearer " + access_token }
    response = requests.get(url=BASE_DOMAIN + "project/v1/hubs/" + hub_id + "/projects/" + proj_id + "/topFolders", headers=headers)
    
    if response.status_code == 200:
        logger.log("Successful retrieval of the top folders' details")
    else:
        if response.status_code == 400:
            logger.log("The request could not be understood by the server due to malformed syntax or missing request headers", True)
        elif response.status_code == 403:
            logger.log("The request was successfully validated but permission is not granted or the application has not been white-listed", True)
        elif response.status_code == 404:
            logger.log("The specified resource was not found", True)
        return proj_id, None, None

    # No need to try/catch this as getting to this point must always be preceeded by a successful retrieval of first folder
    res_data = response.json()['data']
    folder_id = list(filter(lambda x:x["attributes"]["name"]==cloud_array[0], res_data))[0]['id']

    # 3: Find nested folder ID
    for i in range(1, len(cloud_array)):
        response = requests.get(url=BASE_DOMAIN + "data/v1/projects/" + proj_id + "/folders/" + folder_id + "/contents", headers=headers)

        if response.status_code != 200:
            if response.status_code == 400:
                logger.log("The request could not be understood by the server due to malformed syntax or missing request headers", True)
            elif response.status_code == 403:
                logger.log("The request was successfully validated but permission is not granted or the application has not been white-listed", True)
            elif response.status_code == 404:
                logger.log("The specified resource was not found", True)
            return proj_id, None, None

        res_data = response.json()['data']

        folder_id = list(filter(lambda x:x["attributes"]["displayName"]==cloud_array[i], res_data))[0]['id']

    logger.log("Successful retrieval of the folder contents collection associated with the specified folder")

    objectCount = list(filter(lambda x:x["attributes"]["displayName"]==cloud_array[i], res_data))[0]['attributes']['objectCount']

    dictionary = {
        "id": folder_id,
        "name": cloud_array[len(cloud_array) - 1],
        "objectCount": objectCount
    }

    logger.log("Successful retrieval of all needed project and account information, proceeding to save into file")

    if path.isfile(file):
        logger.log("Settings file exists, checking contents...")
        with open(file, "r") as outfile:
            obj = json.load(outfile)

        # check if the project exists
        project_info = list(filter(lambda x:x["name"] == project, obj["projects"]))
        if len(project_info) != 0:
            logger.log("Project exist, appending new folder information...")
            for i, proj in enumerate(obj["projects"]):
                if proj["name"] == project:
                    break
            obj["projects"][i]["folder_data"].append(dictionary)
        else:
            logger.log("Project doesn't exist, adding new project to the settings file...")
            new_project = {
                "hub": hub_id,
                "id": proj_id,
                "name": project,
                "folder_data": []
            }
            new_project["folder_data"].append(dictionary)
            obj["projects"].append(new_project)
        with open(file, "w") as outfile:
            json.dump(obj, outfile, indent=4)
        outfile.close()
        logger.log("Appended information retrieved successfully")
    else:
        logger.log("Settings file doesn't exist, creating and adding data...")
        full_dict = {
            "projects": []
        }
        proj =  {
            "hub": hub_id,
            "id": proj_id,
            "name": project,
            "folder_data": []
        }
        proj["folder_data"].append(dictionary)
        full_dict["projects"].append(proj)
        with open(file, "w") as outfile:
            json.dump(full_dict, outfile, indent=4)
        outfile.close()
        logger.log("Settings file created successfully")
    return proj_id, folder_id, objectCount