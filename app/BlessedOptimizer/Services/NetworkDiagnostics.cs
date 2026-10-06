using System.Net.NetworkInformation;

namespace BlessedOptimizer.Services;

public static class NetworkDiagnostics
{
    public static async Task<string> TestConnectivityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync("1.1.1.1", 2500).WaitAsync(cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? $"Odpowiedź z 1.1.1.1: {reply.RoundtripTime} ms. To pojedynczy test; nie zmieniono ustawień sieci."
                : $"Test nie otrzymał odpowiedzi ({reply.Status}). Nie zmieniono ustawień sieci.";
        }
        catch (OperationCanceledException)
        {
            return "Test został anulowany.";
        }
        catch (PingException)
        {
            return "Nie udało się wykonać testu. Sieć może blokować ping albo być niedostępna. Ustawień nie zmieniono.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return "Nie udało się wykonać testu. Sieć może blokować ping albo być niedostępna. Ustawień nie zmieniono.";
        }
    }
}
