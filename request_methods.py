import requests
import base64
import math
from datetime import datetime
from const import APS_CLIENT_ID, APS_CLIENT_SECRET, BASE_DOMAIN
from logger import Logger

# Call GET /health to check on the Autodesk REST API status
# Successful call returns "200 OK"
def api_up():
    return requests.get(BASE_DOMAIN + "health").status_code == 200

# Call the POST token endpoint specifying the grant_type as client_credentials and the scopes needed
# Successful call returns "200 OK"
def get_two_legged_token(logger: Logger):
    byte_str = (APS_CLIENT_ID + ":" + APS_CLIENT_SECRET).encode("UTF-8")

    token = base64.b64encode(byte_str)

    headers = {"Content-Type": "application/x-www-form-urlencoded",
               "Accept": "application/json",
               "Authorization": "Basic " + token.decode("UTF-8")}
    body = {"grant_type": "client_credentials",
            "scope": "data:read data:write"}
    response = requests.post(url=BASE_DOMAIN + "authentication/v2/token", headers=headers, data=body)

    # check status_code of request
    code = response.status_code
    if code == 200:
      logger.log("Successful request, access token returned")
    else:
      if code == 400:
        logger.log("Invalid request for access token", True)
      elif code == 401:
        logger.log("Invalid credentials for access token", True)
      elif code == 429:
        logger.log("Rate limit exceeded", True)
      else:
        logger.log("Generic internal server error", True)
      raise APIException(code)

    access_token = response.json()["access_token"]
    return access_token

# Call POST projects/:project_id/storage to create an empty storage object for the file in the folder
# Successful call returns "201 Created"
def create_object(logger: Logger, access_token, folder_id, project_id, filename): 
  headers = {'Content-Type': 'application/vnd.api+json',
               'Accept': 'application/vnd.api+json',
               'Authorization': 'Bearer ' + access_token}
    
  body = {"jsonapi": { "version": "1.0" },
          "data": {
            "type": "objects",
            "attributes": {
              "name": filename
            },
            "relationships": {
              "target": {
                "data": { "type": "folders", "id": folder_id }
                }
              }
            }
          }
  response = requests.post(url=BASE_DOMAIN + "data/v1/projects/" + project_id + "/storage", headers=headers, json=body)
  
  # check status_code of request
  code = response.status_code
  if code == 201:
    logger.log("Successful creation of a storage location")
  else:
    logger.log("Failed to create a storage location", True)
    if code == 400:
      logger.log("The request could not be understood by the server due to malformed syntax or missing request headers", True)
    elif code == 403:
      logger.log("The request was successfully validated but permission is not granted or the application has not been white-listed", True)
    else:
      logger.log("The specified resource was not found", True)
    raise APIException(code)

  res_data = response.json()

  object_id = res_data['data']['id']
  object_id_array = object_id.split("/")
  object_key = object_id_array[len(object_id_array) - 1]

  bucket = object_id_array[len(object_id_array) - 2]
  bucket_array = bucket.split(":")
  bucket_key = bucket_array[len(bucket_array) - 1]

  return object_id, object_key, bucket_key

# Call GET buckets/:bucketKey/objects/:objectKey/signeds3upload to generate a signed URL for the storage object
# Successful call returns "200 OK"
def generate_signed_url(logger:Logger, access_token, bucket_key, object_key):
  headers = {'Authorization': 'Bearer ' + access_token }

  response = requests.get(url=BASE_DOMAIN + "oss/v2/buckets/" + bucket_key + "/objects/" + object_key + "/signeds3upload", headers=headers)
    
  # check status_code of request
  code = response.status_code
  if code == 200:
    logger.log("Successful generation of a signed URL")
  else:
    logger.log("Failed to generate a signed URL", True)
    if code == 400:
      logger.log("The request could not be understood by the server due to malformed syntax or missing request headers", True)
    elif code == 401:
      logger.log("The supplied Authorization header was not valid or the supplied token scope was not acceptable", True)
    elif code == 403:
      logger.log("The Authorization was successfully validated but permission is not granted", True)
    elif code == 404:
      logger.log("Object or bucket does not exist", True)
    elif code == 429:
      logger.log("The maximum number of API calls that an app can make per minute was exceeded", True)
    else:
      logger.log("Internal failure while processing the request, reason depends on error", True)
    raise APIException(code)

  res_data = response.json()

  upload_key = res_data['uploadKey']
  urls = res_data['urls'][0]

  return upload_key, urls

# Call PUT method and the urls attribute as the URI to upload the file to the signed URL
# Successful call returns "200"
def upload_file(logger:Logger, urls, path):
  response = requests.put(url=urls, data=open(path, "rb"))

  if response.status_code != 200:
    logger.log("Unable to upload file", True)
    raise APIException(response.status_code)
  else:
    logger.log("Uploaded file successfully")

# Call POST buckets/:bucket_key/objects/:object_key/signeds3upload to complete the upload
# Successful call returns "200 OK"
def complete_upload(logger:Logger, access_token, upload_key, bucket_key, object_key):
    headers = {'Authorization': 'Bearer ' + access_token,
               'Content-Type': 'application/json' }

    body = {"uploadKey": upload_key}

    response = requests.post(url=BASE_DOMAIN + "oss/v2/buckets/" + bucket_key + "/objects/" + object_key + "/signeds3upload", headers=headers, json=body)

    code = response.status_code
    if code == 200:
      logger.log("Upload completed successfully")
    else:
      logger.log("Failed to complete the upload", True)
      if code == 400:
        logger.log("The request could not be understood by the server due to malformed syntax or missing request headers", True)
      elif code == 401:
        logger.log("The supplied Authorization header was not valid or the supplied token scope was not acceptable", True)
      elif code == 403:
        logger.log("The Authorization was successfully validated but permission is not granted", True)
      elif code == 404:
        logger.log("Object or bucket does not exist", True)
      elif code == 429:
        logger.log("The maximum number of API calls that an app can make per minute was exceeded", True)
      else:
        logger.log("Internal failure while processing the request", True)
      raise APIException(code)

# Call POST projects/:project_id/items to create the first version of the uploaded file
# Successful call returns "201 Created"
def create_first_version(logger:Logger, access_token, filename, folder_id, object_id, project_id):
  headers = {'Content-Type': 'application/vnd.api+json',
               'Accept': 'application/vnd.api+json',
               'Authorization': 'Bearer ' + access_token}
  body = {
      "jsonapi": { "version": "1.0" },
      "data": {
        "type": "items",
        "attributes": {
          "displayName": filename,
          "extension": {
            "type": "items:autodesk.bim360:File",
            "version": "1.0"
          }
        },
        "relationships": {
          "tip": {
            "data": {
              "type": "versions", "id": "1"
            }
          },
          "parent": {
            "data": {
              "type": "folders",
              "id": folder_id
            }
          }
        }
      },
      "included": [
        {
          "type": "versions",
          "id": "1",
          "attributes": {
            "name": filename,
            "extension": {
              "type": "versions:autodesk.bim360:File",
              "version": "1.0"
            }
          },
          "relationships": {
            "storage": {
              "data": {
                "type": "objects",
                "id": object_id
               }
             }
           }
         }
        ]
      }
  response = requests.post(url= BASE_DOMAIN + "/data/v1/projects/" + project_id + "/items", headers=headers, json=body)
  
  code = response.status_code
  if code == 201:
    logger.log("Successful creation of a version of a file, or the file was successfully copied")
  else:
    logger.log("Failed to create a version of a file", True)
    if code == 400:
      logger.log("The request could not be understood by the server due to malformed syntax or missing request headers", True)
    elif code == 403:
      logger.log("The request was successfully validated but permission is not granted or the application has not been white-listed", True)
    elif code == 404:
      logger.log("The specified resource was not found", True)
    elif code == 409:
      logger.log("The specified resource already exists, has been modified, or is in the middle of being uploaded, updated, or copied", True)
    else:
      logger.log("The source or destination resource is locked or being modified", True)
    raise APIException(code)
   
# Call POST projects/:project_id/versions to create an additional version of the file
# Successful call returns "201 Created"
def add_additional_version(logger:Logger, access_token, filename, file_id, object_id, project_id):
  headers = {'Authorization': 'Bearer ' + access_token,
               'Content-Type': 'application/vnd.api+json' }
  body = {
            "jsonapi": { 
              "version": "1.0" 
              },
              "data": {
                  "type": "versions",
                  "attributes": {
                      "name": filename,
                      "extension": {
                          "type": "versions:autodesk.bim360:File",
                          "version": "1.0"
                      }
                  },
                  "relationships": {
                      "item": {
                          "data": {
                              "type": "items",
                              "id": file_id
                          }
                      },
                      "storage": {
                          "data": {
                              "type": "objects",
                              "id": object_id
                          }
                      }
                  }
             }
            }
  response = requests.post(url=BASE_DOMAIN + "/data/v1/projects/" + project_id + "/versions", headers=headers, json=body)
  
  code = response.status_code
  if code == 201:
    logger.log("Successful creation of a version of a file")
    return True
  else:
    logger.log("Failed to create a version of a file", True)
    if code == 400:
      logger.log("The request could not be understood by the server due to malformed syntax or missing request headers", True)
    elif code == 403:
      logger.log("The request was successfully validated but permission is not granted or the application has not been white-listed", True)
    elif code == 404:
      logger.log("The specified resource was not found", True)
    elif code == 409:
      logger.log("The specified resource already exists, has been modified", True)
    else:
      logger.log("The source or destination resource is locked or being modified", True)
    return False

# Call GET projects/:project_id/folders/:folder_id/contents to retrieve the id of a certain file if it exists
def get_file_id(logger: Logger, access_token, project_id, folder_id, objectCount, filename):
  headers = {
     "Authorization": "Bearer " + access_token
  }
  
  max_index = math.floor(objectCount / 200)
  for i in range(0, max_index + 1):
    params = { "page[number]": i }
    response = requests.get(url=BASE_DOMAIN + "/data/v1/projects/" + project_id + "/folders/" + folder_id + "/contents", headers=headers, params=params)
    
    code = response.status_code
    if code == 200:
      logger.log("Successful retrieval of the folder contents collection associated with a specific folder")
    else:
      logger.log("Failed to retrieve folder contents", True)
      if code == 400:
        logger.log("The request could not be understood by the server due to malformed syntax or missing request headers", True)
      elif code == 403:
        logger.log("The request was successfully validated but permission is not granted or the application has not been white-listed", True)
      else:
        logger.log("The specified resource was not found", True)
      raise APIException(code)

    res_json = response.json()["data"]
    file = list(filter(lambda x:x["attributes"]["displayName"]==filename, res_json))
  
    if len(file) != 0: # found a file id
      logger.log("File id found successfully")
      return file[0]["id"]
  return None

# Call Get projects/:project_id/items/:item_id to retrieve metadata for a specified item
def check_last_modified_time(logger: Logger, access_token, project_id, folder_id, objectCount, filename):
  try:
    file_id = get_file_id(logger, access_token, project_id, folder_id, objectCount, filename)
  except APIException as exception:
    return False # File not found
  
  if file_id == None:
    return False

  headers = {
     "Authorization": "Bearer " + access_token
  }
  response = requests.get(url=BASE_DOMAIN + "/data/v1/projects/" + project_id + "/items/" + file_id, headers=headers)

  code = response.status_code
  if code == 200:
    logger.log("Successful retrieval of a specific item")
  else:
    logger.log("Failed to retrieve item data", True)
    if code == 400:
      logger.log("The request could not be understood by the server due to malformed syntax or missing request headers", True)
    elif code == 403:
      logger.log("The request was successfully validated but permission is not granted or the application has not been white-listed", True)
    raise APIException(code)

  res_data = response.json()["data"]
  day_mod = res_data["attributes"]["lastModifiedTime"].split("T")[0]
  return datetime.today().strftime('%Y-%m-%d') == day_mod

# Main method flow for a single file upload
def upload_request_flow(logger: Logger, access_token, project_id, folder_id, objectCount, filepath, filename):
  try:
    # Step 1: Check if file has been modified since last update
    if check_last_modified_time(logger, access_token, project_id, folder_id, objectCount, filename):
      logger.log("File to be uploaded has not changed since last time", True)
      return False

    # Step 2: Create a Storage Object
    object_id, object_key, bucket_key = create_object(logger=logger, access_token=access_token, folder_id=folder_id, project_id=project_id, filename=filename) 

    # Step 3: Generate a Signed S3 URL
    upload_key, urls = generate_signed_url(logger=logger, access_token=access_token, bucket_key=bucket_key, object_key=object_key)

    # Step 4: Upload a File to the Signed URL
    upload_file(logger, urls, filepath)

    # Step 5: Complete the Upload
    complete_upload(logger, access_token, upload_key, bucket_key, object_key)

    # Step 6: Get the file id if it exists
    file_id = get_file_id(logger, access_token, project_id, folder_id, objectCount, filename)
    if file_id != None: # if file id is None then it means it couldn't be found so a new version must be created
      # Step 7a: Create Additional Versions of the File
      add_additional_version(logger, access_token, filename, file_id, object_id, project_id)
    else:
      # Step 7b: Create the First Version of the File
      create_first_version(logger, access_token, filename, folder_id, object_id, project_id)
    return True
  except APIException as exception:
    return False

class APIException(Exception):
  def __init__(self, value):
    self.value = value

  def __str__(self):
    return repr(self.value)