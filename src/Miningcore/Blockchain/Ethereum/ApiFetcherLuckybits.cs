using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

//elvalere

public class ApiFetcherLuckybits
{
    private static readonly HttpClient client = new HttpClient();

    // Fonction pour récupérer les valeurs multiplier et base de l'API
    public async Task<(double multiplier, double baseValue)> GetRewardValuesAsync(ulong blockNumber)
    {
        string url = $"https://api.luckybits.org/reward/?block={blockNumber}";

        try
        {
            // Faire la requête GET à l'API
            HttpResponseMessage response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode(); // Lève une exception si le code de statut n'est pas 2xx

            // Lire la réponse JSON
            string responseBody = await response.Content.ReadAsStringAsync();

            // Parse le JSON et récupérer les valeurs
            JObject json = JObject.Parse(responseBody);
            double multiplier = json["multiplier"].ToObject<double>();
            double baseValue = json["base"].ToObject<double>();

            return (multiplier, baseValue);
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"Erreur lors de la requête HTTP : {e.Message}");
            return (0, 0); // Valeurs par défaut en cas d'erreur
        }
    }
}
