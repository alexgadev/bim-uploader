using System;
using System.Diagnostics.Contracts;
using System.IO;

namespace bim_push
{
    public class Logger
    {
        private const int deletion_threshold = 7; // change this to modify the number of days a log file lives

        private int errors;
        private readonly string log_directory; // log directory sits right in the same level of the executable
        private readonly StreamWriter file_controller;

        public Logger() 
        {
            log_directory = Path.Combine(Environment.ProcessPath.Replace("bim-push.exe", ""), "logs");

            // file name is just the day of creation
            string path = Path.Combine(log_directory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            
            file_controller = new StreamWriter(path);
            
            ManageLogs(); // run this here so it appears at the top of the log file
            
            errors = 0;
        }

        public void Dispose()
        {
            file_controller.WriteLine("Number of errors: " + this.errors.ToString() + "\n");
            file_controller.Close();
        }

        public void Log(string message, bool error = false)
        {
            message = DateTime.Now.ToShortTimeString() + message;
            
            if (error)
            {
                this.errors++;
                message = "[ERROR] -> " + message;
            }
            this.file_controller.WriteLine(message);
        }

        private void ManageLogs()
        {
            string[] files = Directory.GetFiles(log_directory);

            foreach (string file in files)
            {
                try
                {
                    if (File.GetCreationTime(file) <= DateTime.Now.AddDays(-deletion_threshold))
                    {
                        Log("File: " + file + " deleted due to being older than threshold of " + deletion_threshold + " days.");
                        File.Delete(file);
                    }
                }
                catch (Exception) 
                {
                    Log("Failed to delete log file: " + file, true);
                }
            }
        }
    }
}
