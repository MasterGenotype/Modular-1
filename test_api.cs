using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;

class Program
{
    static async Task Main()
    {
        var apiKey = Environment.GetEnvironmentVariable("NEXUS_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("ERROR: Set NEXUS_API_KEY environment variable");
            return;
        }

        using var client = new HttpClient();
        client.BaseAddress = new Uri("https://api.nexusmods.com");
        client.DefaultRequestHeaders.Add("apikey", apiKey);
        client.DefaultRequestHeaders.Add("accept", "application/json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Modular/1.0");

        try
        {
            // Test validate endpoint
            var validateResponse = await client.GetAsync("/v1/users/validate.json");
            Console.WriteLine($"Validate: {validateResponse.StatusCode}");
            if (validateResponse.IsSuccessStatusCode)
            {
                var content = await validateResponse.Content.ReadAsStringAsync();
                var json = JsonDocument.Parse(content);
                Console.WriteLine($"Premium: {json.RootElement.GetProperty("is_premium").GetBoolean()}");
            }

            // Test download link endpoint
            var downloadResponse = await client.GetAsync("/v1/games/stardewvalley/mods/4/files/153195/download_link.json");
            Console.WriteLine($"\nDownload Link: {downloadResponse.StatusCode}");
            if (downloadResponse.IsSuccessStatusCode)
            {
                var content = await downloadResponse.Content.ReadAsStringAsync();
                Console.WriteLine("Success! Got download links");
            }
            else
            {
                var error = await downloadResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"Error: {error}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception: {ex.Message}");
        }
    }
}
