using System;
using System.Net.Http;
using System.Threading.Tasks;

public class IpService
{
    public async Task<string> GetExternalIPAddressAsync()
    {
        using (HttpClient client = new HttpClient())
        {
            try
            {
                // Service en ligne pour récupérer l'IP publique
                string ip = await client.GetStringAsync("http://api.ipify.org");
                return ip.Trim();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Erreur lors de la récupération de l'IP externe : " + ex.Message);
                return "Non disponible";
            }
        }
    }
}
