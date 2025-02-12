from datetime import datetime
from os import path, walk, listdir, unlink

class Logger:
    def __init__(self) -> None:
        # assign log path to be in the same directory with name as the date of execution
        self.path = __file__.replace("logger.py", "") + "logs\\"  + datetime.today().strftime('%Y-%m-%d') + ".log"

        # open file to read the number of executions that have occurred
        self.file = open(self.path, "a")
        
        # TODO: self.threshold = 3 
        self.errors = 0
        #TODO: multiple file upload fails, threshold of errors will take control, else all errors will be logged

    #
    def log(self, message, error=False):
        message = datetime.now().strftime("%H:%M:%S") + " - " + message
        if error:
            self.errors += 1
            message = message + " -> [ERROR]"
        self.file.write(message + "\n")

    # 
    def manage_logs(self):
        dir = __file__.replace("logger.py", "") + "logs\\"
        if len([name for name in listdir(dir) if path.isfile(path.join(dir, name))]) == 7:
            for filename in listdir(dir):
                file_path = path.join(dir, filename)
                try:
                    unlink(file_path)
                except Exception as e:
                    self.log("Failed to delete logs", True)

    # Destroy closes the file source and sends email if last execution happens to occurr
    def destroy(self):
        self.file.write("Number of errors: " + str(self.errors) + "\n\n")
        self.manage_logs()
        self.file.close()
