using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace NCEIData
{
    class clsNLDAS_GES_DISC
    {
        private readonly HttpClient _httpClient;
        private const string TimeSeriesUrl = "https://api.giovanni.earthdata.nasa.gov/timeseries";
        private const string UserAgent = "GESDISC.Net v1.0";

        public clsNLDAS_GES_DISC()
        {
            var handler = new HttpClientHandler()
            {
                UseCookies = true,
                CookieContainer = new System.Net.CookieContainer()
            };
            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        }

        /// <summary>
        /// Reads NASA EarthData credentials from .netrc file
        /// </summary>
        /// <returns>Tuple containing username and password</returns>
        private (string username, string password) ReadNetrcCredentials()
        {
            string netrcPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".netrc");

            if (!File.Exists(netrcPath))
            {
                throw new FileNotFoundException("No .netrc file found. Please create one with your EarthData credentials.");
            }

            var lines = File.ReadAllLines(netrcPath);
            string? username = null;
            string? password = null;

            foreach (string line in lines)
            {
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 2)
                {
                    if (tokens[0] == "machine" && tokens[1].Contains("earthdata.nasa.gov"))
                    {
                        // Continue reading for login/password on next lines or same line
                        continue;
                    }
                    else if (tokens[0] == "login")
                        username = tokens[1];
                    else if (tokens[0] == "password")
                        password = tokens[1];
                }
            }

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException("Could not find valid EarthData credentials in .netrc file.");
            }

            return (username, password);
        }

        /// <summary>
        /// Authenticates with NASA EarthData and gets an OAuth access token
        /// </summary>
        /// <returns>Access token for API requests</returns>
        public async Task<string> GetAccessTokenAsync()
        {
            try
            {
                var (username, password) = ReadNetrcCredentials();

                // Use the find_or_create_token endpoint like the Python earthaccess library
                string? accessToken = await FindOrCreateTokenAsync(username, password);

                if (string.IsNullOrEmpty(accessToken))
                {
                    throw new InvalidOperationException("Failed to obtain access token");
                }

                return accessToken;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Authentication with NASA EarthData failed. " +
                    "Please ensure that your .netrc file is stored and contains valid credentials. " +
                    $"Error: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Finds or creates a token using the same endpoint as Python earthaccess
        /// </summary>
        private async Task<string?> FindOrCreateTokenAsync(string username, string password)
        {
            try
            {
                // This matches the Python earthaccess implementation exactly
                var findOrCreateTokenUrl = "https://urs.earthdata.nasa.gov/api/users/find_or_create_token";

                var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));

                var request = new HttpRequestMessage(HttpMethod.Post, findOrCreateTokenUrl);
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
                request.Headers.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                var response = await _httpClient.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    var tokenResponse = JObject.Parse(responseContent);
                    return tokenResponse["access_token"]?.ToString();
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"Token request failed with status {response.StatusCode}: {errorContent}");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to find or create token: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Calls the Giovanni time series API
        /// </summary>
        /// <param name="lat">Latitude</param>
        /// <param name="lon">Longitude</param>
        /// <param name="timeStart">Start time in YYYY-MM-DDThh:mm:ss format (UTC)</param>
        /// <param name="timeEnd">End time in YYYY-MM-DDThh:mm:ss format (UTC)</param>
        /// <param name="dataVariable">Name of the data parameter for the time series</param>
        /// <param name="accessToken">Authentication token</param>
        /// <returns>CSV time series data as string</returns>
        public async Task<string> CallTimeSeriesAsync(double lat, double lon, string timeStart, string timeEnd, string dataVariable, string accessToken)
        {
            var queryParams = new Dictionary<string, string>
            {
                ["data"] = dataVariable,
                ["location"] = $"[{lat},{lon}]",
                ["time"] = $"{timeStart}/{timeEnd}"
            };

            var uriBuilder = new UriBuilder(TimeSeriesUrl);
            var query = string.Join("&", queryParams.Select(kvp => $"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
            uriBuilder.Query = query;

            var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            var response = await _httpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException($"API request failed with status {response.StatusCode}: {errorContent}");
            }

            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>
        /// Parses CSV time series data
        /// </summary>
        /// <param name="csvData">Raw CSV data from time series API</param>
        /// <returns>Tuple containing headers dictionary and data rows</returns>
        public (Dictionary<string, string> headers, List<TimeSeriesDataPoint> dataPoints) ParseCsv(string csvData)
        {
            var lines = csvData.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var headers = new Dictionary<string, string>();
            var dataPoints = new List<TimeSeriesDataPoint>();

            if (lines.Length < 15)
            {
                throw new InvalidOperationException(
                    "The returned CSV is empty or incomplete.\n" +
                    "Please ensure that your subsetting bounds are within the extent of your dataset\n" +
                    "or that your .netrc file is stored and contains valid credentials.");
            }

            // Parse headers (first 13 rows)
            for (int i = 0; i < 13 && i < lines.Length; i++)
            {
                var parts = lines[i].Split(',', 2);
                if (parts.Length >= 2)
                {
                    headers[parts[0]] = parts[1].Trim();
                }
            }

            // Skip the column header row (row 13) and data header row (row 14)
            for (int i = 15; i < lines.Length; i++)
            {
                var parts = lines[i].Split(',');
                if (parts.Length >= 2)
                {
                    if (DateTime.TryParse(parts[0], out var timestamp) &&
                        double.TryParse(parts[1], out var value))
                    {
                        dataPoints.Add(new TimeSeriesDataPoint
                        {
                            Timestamp = timestamp,
                            Value = value
                        });
                    }
                }
            }

            return (headers, dataPoints);
        }

        /// <summary>
        /// Complete workflow to get time series data from NLDAS
        /// </summary>
        /// <param name="lat">Latitude</param>
        /// <param name="lon">Longitude</param>
        /// <param name="timeStart">Start time in YYYY-MM-DDThh:mm:ss format</param>
        /// <param name="timeEnd">End time in YYYY-MM-DDThh:mm:ss format</param>
        /// <param name="dataVariable">Data variable name (default: NLDAS_FORA0125_H_2_0_Rainf)</param>
        /// <returns>Parsed time series data</returns>
        public async Task<(Dictionary<string, string> headers, List<TimeSeriesDataPoint> dataPoints)>
            GetTimeSeriesDataAsync(double lat, double lon, string timeStart, string timeEnd,
            string dataVariable = "NLDAS_FORA0125_H_2_0_Rainf")
        {
            var accessToken = await GetAccessTokenAsync();
            var csvData = await CallTimeSeriesAsync(lat, lon, timeStart, timeEnd, dataVariable, accessToken);
            return ParseCsv(csvData);
        }

        /// <summary>
        /// Saves time series data to CSV file
        /// </summary>
        /// <param name="headers">Metadata headers</param>
        /// <param name="dataPoints">Time series data points</param>
        /// <param name="filePath">Output file path</param>
        public async Task SaveToCsvAsync(Dictionary<string, string> headers, List<TimeSeriesDataPoint> dataPoints,
            string filePath)
        {
            using var writer = new StreamWriter(filePath);

            // Write headers as metadata
            foreach (var header in headers)
            {
                await writer.WriteLineAsync($"{header.Key},{header.Value}");
            }

            // Write empty line
            await writer.WriteLineAsync();

            // Write column headers
            await writer.WriteLineAsync("Timestamp,Value");

            // Write data
            foreach (var point in dataPoints)
            {
                await writer.WriteLineAsync($"{point.Timestamp:yyyy-MM-ddTHH:mm:ss},{point.Value}");
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    /// <summary>
    /// Represents a single time series data point
    /// </summary>
    public class TimeSeriesDataPoint
    {
        public DateTime Timestamp { get; set; }
        public double Value { get; set; }
    }
}
