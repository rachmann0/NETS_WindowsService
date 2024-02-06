using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json;

namespace NETS_WindowsService
{
    public partial class Service1 : ServiceBase
    {
        public Service1()
        {
            InitializeComponent();
        }

        readonly HttpClient httpClient = new HttpClient();
        static string username = null, password = null, baseURL = null;
        public class Config
        {
            public string Username { get; set; }
            public string Password { get; set; }
            public string BaseUrl { get; set; }
        }
        static readonly string logFileName = "logs.txt";

        static void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            // This event is triggered when data is received from the serial port
            SerialPort sp = (SerialPort)sender;
            string data = sp.ReadExisting(); // Read the received data
            AppendToLogFile($"Received data: {data}");
        }

        async protected override void OnStart(string[] args)
        {
            // CLEAR LOG IF LAST LOG IS FROM YESTERDAY
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string logFileName = "logs.txt";
            string logFilePath = Path.Combine(baseDirectory, logFileName);
            if (!File.Exists(logFileName)) {
                // Create the log file if it doesn't exist
                File.WriteAllText(logFileName, "");
                Console.WriteLine($"File '{logFileName}' created.");
            }
            ClearLogFileIfLatestEntryIsFromYesterday(logFileName);

            // INIT SERIAL PORT
            string[] ports = SerialPort.GetPortNames();
            if (ports.Length == 0)
            {
                AppendToLogFile("No serial ports found.");
                return;
            }
            // Select the first available serial port
            string selectedPort = ports[0];
            SerialPort serialPort = new SerialPort(selectedPort, 9600); // Specify the baud rate
            serialPort.Open();

            try
            {

            // GET CONFIG
            AppendToLogFile("NETS Service Start");
            // Get the directory of the executable
            string fileName = "config.txt";
            string filePath = Path.Combine(baseDirectory, fileName);
            if (!File.Exists(filePath)) {
                AppendToLogFile($"File not found: {fileName}");
                return;
            }
            // Read the contents of the file
            string contents = File.ReadAllText(filePath);
            // Parse the contents to extract username and password
            string json = contents;
            AppendToLogFile(contents);
            Config config = JsonSerializer.Deserialize<Config>(json);
            username = config.Username;
            password = config.Password;
            baseURL = config.BaseUrl;
            // Display the extracted values
            AppendToLogFile("Username: " + username);
            AppendToLogFile("Password: " + password);
            AppendToLogFile("Password: " + baseURL);


            AppendToLogFile($"Serial port {selectedPort} opened.");

            // Attach an event handler for receiving data
            serialPort.DataReceived += SerialPort_DataReceived;

        // Write data to the serial port
/*        string dataToSend = "Hello, Serial Port!";
        byte[] buffer = System.Text.Encoding.UTF8.GetBytes(dataToSend);
        serialPort.Write(buffer, 0, buffer.Length);
        AppendToLogFile("Binary Data Sent Successfully.");
*/

/*        Task loopTask = Task.Run(async () =>
            {
*/
            string credentials = $"{username}:{password}";
            byte[] bytes = Encoding.ASCII.GetBytes(credentials);
            string base64 = Convert.ToBase64String(bytes);
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Basic {base64}");
            while (true)
            {
                //AppendToLogFile("send binary data");
                        // Get the latest request
                        string apiUrl = baseURL + "/SC_NETS_IS/rest/NETS/GetNewRequests";
                        HttpResponseMessage response = await httpClient.GetAsync(apiUrl);
                        string responseData = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode) continue;
    /*                    AppendToLogFile(responseData);
    */
                        // Deserialize JSON response
                        JsonDocument document = JsonDocument.Parse(responseData);
                        // Access the root element
                        JsonElement root = document.RootElement;

                        int ECN = GetPropertyValue<int>(root, "ECN");
                        string newRequestMessage = GetPropertyValue<string>(root, "Message");

                        AppendToLogFile($"ECN: {ECN}");
                        AppendToLogFile($"newRequestMessage: {newRequestMessage}");


                        // Check if ECN is not zero
                        if (ECN != 0)
                        {
                            string apiUrl2 = baseURL + "/SC_NETS_IS/rest/NETS/UpdateMessageIsReceived";
                            string postData = ECN.ToString();

                            // Send a POST request to update message received status
                            HttpResponseMessage response2 = await httpClient.PostAsync(apiUrl2, new StringContent(postData, Encoding.UTF8, "application/json"));

                            if (response2.IsSuccessStatusCode && !string.IsNullOrEmpty(newRequestMessage))
                            {
                                // Send binary data to serial port
                                //await SendBinaryDataToSerialPort(serialPort, newRequestMessage);
                            }
                        }

                        await Task.Delay(10*1000);
            }


            }
            catch (Exception ex)
            {
            AppendToLogFile($"Error: {ex.Message}");
            }
            finally
            {
            // Close the serial port when done
            if (serialPort.IsOpen)
                serialPort.Close();

            AppendToLogFile("Serial port closed.");
            }
        }

        static string FormatDateTimeToString(DateTime dateTime)
        {
            string format = "yyyy-MM-dd HH:mm:ss"; // Example: "2022-02-05 15:30:00"
            string dateString = dateTime.ToString(format);
            return dateString;
        }

        static void AppendToLogFile(string content)
        {
            string fileName = logFileName;
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            string filePath = Path.Combine(directory, fileName);
            // Append content to the file or create it if it doesn't exist
            using (StreamWriter writer = new StreamWriter(filePath, true)) // true for append mode
            {
                // Write the content to the file
                writer.WriteLine($"{FormatDateTimeToString(DateTime.Now)};{content}");
            }
        }


        static void ClearLogFileIfLatestEntryIsFromYesterday(string logFileName)
        {
            // Read all lines from the log file
            string[] lines = File.ReadAllLines(logFileName);

            // Check if there are any log entries
            if (lines.Length > 0)
            {
                // Get the timestamp from the latest log entry
                string latestLogEntry = lines.Last();
                DateTime latestLogTimestamp = ParseLogTimestamp(latestLogEntry);

                // Check if the latest log entry is from yesterday
                if (latestLogTimestamp.Date < DateTime.Today)
                {
                    // Clear the log file
                    ClearLogFile(logFileName);
                }
            }
        }
        static DateTime ParseLogTimestamp(string logEntry)
        {
            // Extract the timestamp from the log entry and parse it
            string[] parts = logEntry.Split(';');
            string timestampStr = parts[0];
            return DateTime.ParseExact(timestampStr, "yyyy-MM-dd HH:mm:ss", null);
        }
        static void ClearLogFile(string logFileName)
        {
            try
            {
                // Clear the contents of the log file
                File.WriteAllText(logFileName, "");
                Console.WriteLine("Log file cleared.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error clearing log file: {ex.Message}");
            }
        }
        static T GetPropertyValue<T>(JsonElement element, string propertyName)
        {
            // Try to get the property value, return default value if property does not exist
            if (element.TryGetProperty(propertyName, out JsonElement property))
            {
                // Check if the property has a value
                if (property.ValueKind != JsonValueKind.Null)
                {
                    // Convert the property value to the desired type
                    return (T)Convert.ChangeType(property.GetRawText().Trim('"'), typeof(T));
                }
            }

            // Return default value if property does not exist or has a null value
            return default;
        }


        protected override void OnStop()
        {
            AppendToLogFile("NETS Service Stop");
        }
    }
}
