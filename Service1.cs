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

        TaskCompletionSource<string> tcs = new TaskCompletionSource<string>();
        SerialPort serialPort;
        byte[] latestBinaryDataSent = { };
        bool isRecieveACK = false;
        bool isReqMessageResolved = true;
        string latestPartialMessage = "";
        string completeMessage = "";
        async Task SendBinaryDataToSerialPortAsync(SerialPort serialPort, byte[] newRequestMessage)
        {
            isReqMessageResolved = false;
            latestBinaryDataSent = newRequestMessage;
            int reIssueAttempts = 0;
            // Open the serial port asynchronously
            if (!serialPort.IsOpen)
            {
                serialPort.Open();
            }
            
            Task<string> Promise()
            {
                // Send the binary data to the serial port
                serialPort.Write(newRequestMessage, 0, newRequestMessage.Length);
                AppendToLogFile($"Binary Data Sent: {BinaryToHex(newRequestMessage)}");
                return tcs.Task;
            }

            try {
                string code = await Promise();
                AppendToLogFile($"code: {code}");
            while ((code == "no-response" || code == "NACK") && reIssueAttempts < 2) {
                code = await Promise();
                AppendToLogFile($"code: {code}");
                reIssueAttempts++;
            }
        } catch (Exception ex) {
            AppendToLogFile($"Error: {ex.Message}");
        } finally {
            // reset statuses for next request
            isRecieveACK = false;
            isReqMessageResolved = true;
            latestBinaryDataSent = null;
            latestPartialMessage = null;
            completeMessage = "";
            AppendToLogFile("request resolved");
        }

        // Close the serial port after sending data
        //serialPort.Close();
        }

        async void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            // This event is triggered when data is received from the serial port
            SerialPort sp = (SerialPort)sender;
        /*    string dataAscii = sp.ReadExisting(); // Read the received data
            byte[] data = Encoding.UTF8.GetBytes(dataAscii);
        */
            int bytesToRead = sp.BytesToRead;
            byte[] buffer = new byte[bytesToRead];
            sp.Read(buffer, 0, bytesToRead);
            string newResponse = BinaryToHex(buffer);

            //Console.WriteLine($"Received data: {newResponse}");
            string hexNACK = "15";
            string hexACK = "06";
            if (newResponse == hexACK)
            {
                isRecieveACK = true;
            }
            else if (newResponse == hexNACK)
            {
                tcs.SetResult(hexNACK);
            }
            else
            {
                if (isRecieveACK)
                {
                    if (latestPartialMessage == "")
                    {
                        latestPartialMessage = newResponse;
                        completeMessage = latestPartialMessage;
                    }
                    else
                    {
                        latestPartialMessage = newResponse;
                        completeMessage = completeMessage + "-" + latestPartialMessage;
                    }
                }
                // IF REACHED 1C SEPERATOR AND 03 STX RESOLVE
                if (!(completeMessage.Length >= 8)) return;
                if (completeMessage.Substring(completeMessage.Length - 8, 5).ToUpper().Contains("1C-03"))
                {
                    AppendToLogFile($"completeMessage: {completeMessage}");
                    string apiUrl = baseURL + "/SC_NETS_IS/rest/NETS/TerminalResponse";
                    var data = new
                    {
                        RequestMessage = BinaryToHex(latestBinaryDataSent),
                        TerminalResponse = completeMessage
                    };
                    string postData = JsonSerializer.Serialize(data);
                    HttpResponseMessage response = await httpClient.PostAsync(apiUrl, new StringContent(postData, Encoding.UTF8, "application/json"));
                    AppendToLogFile(response.IsSuccessStatusCode.ToString());
                    if (!response.IsSuccessStatusCode) return;

                    string responseData = await response.Content.ReadAsStringAsync();
                    if (bool.Parse(responseData))
                    {
                        SendACK(serialPort);
                        tcs.SetResult("RESOLVED");
                    }
        /*            JsonDocument document = JsonDocument.Parse(responseData);
                    JsonElement root = document.RootElement;
                    int ECN = GetPropertyValue<int>(root, "ECN");
                    string newRequestMessage = GetPropertyValue<string>(root, "Message");
        */            
                }
            }
        }

        async protected override void OnStart(string[] args)
        {
            // CLEAR LOG IF LAST LOG IS FROM YESTERDAY
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string logFileName = "logs.txt";
            string logFilePath = Path.Combine(baseDirectory, logFileName);
            if (!File.Exists(logFilePath)) {
                // Create the log file if it doesn't exist
                File.WriteAllText(logFilePath, "");
                Console.WriteLine($"File '{logFilePath}' created.");
            }
            ClearLogFileIfOldestEntryIsFromYesterday(logFilePath);

            // INIT SERIAL PORT
            string[] ports = SerialPort.GetPortNames();
            if (ports.Length == 0)
            {
                AppendToLogFile("No serial ports found.");
                return;
            }
            // Select the first available serial port
            string selectedPort = ports[0];
            serialPort = new SerialPort(selectedPort, 9600); // Specify the baud rate
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
            //AppendToLogFile(contents);
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
            serialPort.ErrorReceived += SerialPort_ErrorReceived;

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
                        // Decode the Base64 string to a byte array
                        if (!string.IsNullOrEmpty(newRequestMessage))
                        {
                            byte[] reqMessageBytes = Convert.FromBase64String(newRequestMessage);
                            newRequestMessage = BinaryToHex(reqMessageBytes);
                        }

                        //AppendToLogFile($"ECN: {ECN}");
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
                                await SendBinaryDataToSerialPortAsync(serialPort, HexToBinary(newRequestMessage));
                            }
                        }

                        await Task.Delay(5*1000);
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

        static void SendACK(SerialPort serialPort)
        {
            byte[] ACK = HexToBinary("06");
            serialPort.Write(ACK, 0, ACK.Length);

            Console.WriteLine("ACK sent successfully.");
        }
        static void SendNACK(SerialPort serialPort)
        {
            byte[] NACK = HexToBinary("15");
            serialPort.Write(NACK, 0, NACK.Length);

            Console.WriteLine("NACK sent successfully.");
        }
        void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            // Handle serial port errors
            SerialPort serialPort = (SerialPort)sender;
            AppendToLogFile($"Error received from serial port {serialPort.PortName}: {e.EventType}");
        }

        static string BinaryToHex(byte[] binaryData)
        {
            return BitConverter.ToString(binaryData);
        }

        static byte[] HexToBinary(string hexString)
        {
            hexString = hexString.Replace("-", ""); // Remove hyphens
            if (IsHexString(hexString))
            {
                byte[] binaryData = new byte[hexString.Length / 2];
                for (int i = 0; i < binaryData.Length; i++)
                {
                    binaryData[i] = Convert.ToByte(hexString.Substring(i * 2, 2), 16);
                }
                return binaryData;
            }
            else
            {
                return new byte[0]; // Return empty byte array if input is not a valid hexadecimal string
            }
        }

        static bool IsHexString(string value)
        {
            foreach (char c in value)
            {
                if (!Uri.IsHexDigit(c))
                {
                    return false;
                }
            }
            return true;
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

        static void ClearLogFileIfOldestEntryIsFromYesterday(string logFilePath)
        {
            // Read all lines from the log file
            string[] lines = File.ReadAllLines(logFilePath);

            AppendToLogFile($"lines: {lines.Length}");
            // Check if there are any log entries
            if (lines.Length > 0)
            {
                // Get the timestamp from the latest log entry
                //string latestLogEntry = lines.Last();
                string latestLogEntry = lines.First();
                DateTime latestLogTimestamp = ParseLogTimestamp(latestLogEntry);

                AppendToLogFile(latestLogTimestamp.Date.ToString());
                AppendToLogFile(DateTime.Today.ToString());
                // Check if the latest log entry is from yesterday
                if (latestLogTimestamp.Date < DateTime.Today)
                {
                    // Clear the log file
                    ClearLogFile(logFilePath);
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
        static void ClearLogFile(string logFilePath)
        {
            try
            {
                // Clear the contents of the log file
                File.WriteAllText(logFilePath, "");
                AppendToLogFile("Log file cleared.");
            }
            catch (Exception ex)
            {
                AppendToLogFile($"Error clearing log file: {ex.Message}");
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
            return default; // 0 if int
        }


        protected override void OnStop()
        {
            AppendToLogFile("NETS Service Stop");
        }
    }
}
