using System.Net.NetworkInformation;

namespace BlessedOptimizer.Services;

public static class NetworkDiagnostics
{
    private const string TestAddress = "1.1.1.1";
    private const int SampleCount = 10;
    private const int PingTimeoutMilliseconds = 900;
    private static readonly TimeSpan SampleDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Runs a short series of read-only ICMP probes and summarizes latency,
    /// variation, and packet loss. No network settings are changed.
    /// </summary>
    public static async Task<string> TestConnectivityAsync(CancellationToken cancellationToken = default)
    {
        var roundTripTimes = new List<long>(SampleCount);
        var lost = 0;

        try
        {
            using var ping = new Ping();
            for (var sample = 0; sample < SampleCount; sample++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var reply = await ping.SendPingAsync(TestAddress, PingTimeoutMilliseconds)
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success)
                        roundTripTimes.Add(reply.RoundtripTime);
                    else
                        lost++;
                }
                catch (PingException)
                {
                    lost++;
                }
                catch (InvalidOperationException)
                {
                    lost++;
                }

                if (sample < SampleCount - 1)
                    await Task.Delay(SampleDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return "Test został anulowany. Nie zmieniono ustawień sieci.";
        }
        catch (PingException)
        {
            return "Nie udało się uruchomić testu. Sieć lub zapora może blokować ping. Nie zmieniono ustawień sieci.";
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            return "Nie udało się uruchomić testu. Sieć lub zapora może blokować ping. Nie zmieniono ustawień sieci.";
        }

        var received = roundTripTimes.Count;
        var lossPercent = (int)Math.Round(lost * 100d / SampleCount);
        if (received == 0)
            return $"Brak odpowiedzi w {SampleCount} próbach do {TestAddress}. Ping może być blokowany przez sieć lub zaporę; sam ten wynik nie dowodzi braku internetu. Nie zmieniono ustawień sieci.";

        var average = roundTripTimes.Average();
        var minimum = roundTripTimes.Min();
        var maximum = roundTripTimes.Max();
        var jitter = roundTripTimes.Count < 2
            ? 0
            : roundTripTimes.Zip(roundTripTimes.Skip(1), (first, next) => Math.Abs(next - first)).Average();

        var quality = lossPercent >= 20 || jitter >= 25
            ? "Połączenie może być niestabilne."
            : lossPercent == 0 && average <= 60 && jitter <= 12
                ? "Wynik wygląda stabilnie."
                : "To pojedynczy kierunek testu — porównaj wynik z tym, co dzieje się w aplikacji.";

        return $"{received}/{SampleCount} odpowiedzi · średnio {average:0} ms (min. {minimum} / maks. {maximum} ms) · wahania {jitter:0} ms · utrata {lossPercent}%. {quality} Nie zmieniono ustawień sieci.";
    }
}
