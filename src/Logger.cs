using System;
using System.Diagnostics.Contracts;
using System.IO;

namespace bim_push
{
    /// <summary>
    /// Represents a logger that writes log messages to a file. The log files are stored in a "logs" directory at the same level as the executable. 
    /// The logger manages log files by deleting those older than a specified threshold (in days).
    /// </summary>
    public class Logger
    {
        private const int deletion_threshold = 7; // change this to modify the number of days a log file lives
        private int errors;
        private readonly string log_directory; // log directory sits right in the same level of the executable
        private readonly StreamWriter file_controller;

        public Logger(string project) 
        {
            log_directory = Path.Combine(Environment.ProcessPath.Replace("bim-push.exe", ""), "logs");

            // file name is the day of creation plus an underscore and the project name, e.g., 2026-03-15_projectName.log
            string path = Path.Combine(log_directory, DateTime.Now.ToString("yyyy-MM-dd") + "_" + project + ".log");
            
            file_controller = new StreamWriter(path, append: true);
            
            ManageLogs(); // run this here so it appears at the top of the log file
            
            errors = 0;
        }

        /// <summary>
        /// Disposes the Logger instance by writing the number of errors to the log file and closing the StreamWriter.
        /// </summary>
        public void Dispose()
        {
            file_controller.WriteLine(DateTime.Now.ToShortTimeString() + ": " + "Number of errors: " + this.errors.ToString() + "\n");
            file_controller.Close();
        }

        /// <summary>
        /// Logs a message to the log file. If the error flag is set, it increments the error count and prefixes the message with "[ERROR] -> ".
        /// </summary>
        /// <param name="message">The message to log.</param>
        /// <param name="error">Indicates whether the message is an error.</param>
        public void Log(string message, bool error = false)
        {
            message = DateTime.Now.ToShortTimeString() + ": " + message;
            
            if (error)
            {
                this.errors++;
                message = "[ERROR] -> " + message;
            }
            this.file_controller.WriteLine(message);
        }

        /// <summary>
        /// Deletes log files older than the deletion threshold (in days) to manage disk space.
        /// </summary>
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
