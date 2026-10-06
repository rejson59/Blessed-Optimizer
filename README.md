# Blessed Optimizer · strona i aplikacja Windows v1.0.2

Responsywna, polskojęzyczna strona projektu z osobnymi zakładkami „O programie” i „Symulator”, niebieskim gradientem, szklistymi panelami oraz gołębiem Blessed. Interaktywny podgląd odwzorowuje układ rzeczywistej aplikacji Windows; liczby i odczyty na stronie są demonstracyjne, a witryna nie diagnozuje urządzenia.

W katalogu `app/BlessedOptimizer/` znajduje się pierwsza natywna aplikacja Windows. Jej numer wersji to **1.0.2**. GitHub Actions buduje ją jako jeden samodzielny plik `.exe`; samo pomyślne zbudowanie nie publikuje jednak wydania. Instalator staje się publicznie dostępny dopiero po opublikowaniu GitHub Release z tagiem `v1.0.2`.

## Co robi aplikacja v1.0.2

- **Interfejs:** cztery główne kategorie — Strefa gracza, Połączenia, Propozycje i Personalizacja Windows — oraz osobna grupa dodatkowych narzędzi: Procesy, Autostart i Zasilanie. Układ odpowiada interaktywnemu podglądowi na stronie.
- **Instalator w tym samym pliku `.exe`:** instalacja tylko dla bieżącego użytkownika do `%LOCALAPPDATA%\Programs\BlessedOptimizer`, skrót w menu Start i opcjonalny skrót na pulpicie. Bez uprawnień administratora, autostartu ani automatycznego zamykania aplikacji.
- **Lokalny przegląd urządzenia:** Windows, nazwa procesora, liczba logicznych wątków, pamięć RAM, nazwa karty graficznej, wolne miejsce na dysku systemowym i widoczne karty sieciowe. Te odczyty nie są wysyłane do usługi Blessed.
- **Procesy:** tabela lokalnych procesów z PID, szacowanym użyciem CPU (z kolejnych próbek), pamięcią roboczą i czasem uruchomienia; wyszukiwanie po nazwie lub PID i odświeżanie co 2 sekundy tylko na otwartej karcie. Jest to podgląd — aplikacja nie kończy procesów ani nie zmienia ich priorytetów. Dostęp do chronionych procesów może być ograniczony przez Windows.
- **Strefa gracza:** po włączeniu pokazuje ogólne użycie CPU i RAM co dwie sekundy. To nie jest pomiar FPS ani temperatury i nie identyfikuje procesów gry. Czuwanie zatrzymuje się po wyjściu z tej sekcji lub zamknięciu programu. Niczego nie zamyka.
- **Autostart:** odczyt i zarządzanie wpisami `Run`/`RunOnce` bieżącego użytkownika. Przed wyłączeniem aplikacja pokazuje polecenie, pyta o zgodę i zachowuje kopię; przywracanie nie uruchamia programu od razu. Nie zmienia autostartu maszynowego, usług ani zadań systemowych.
- **Zaawansowane zasilanie:** odczyt aktywnego planu Windows oraz dostępnych ustawień maksymalnego stanu procesora, oszczędzania energii USB i łącza PCIe — osobno dla zasilania z sieci i baterii. Zmiana wymaga potwierdzenia w aplikacji i osobnego monitowania UAC, zapisuje oryginalne wartości lokalnie i oferuje ich przywrócenie. Pomocnik odmawia zapisu, jeśli podczas UAC aktywny plan lub konto Windows zmieniły się. Wpływ na wydajność, energię i responsywność jest opisany przy opcji; aplikacja nie ingeruje w limity termiczne ani zabezpieczenia.
- **Połączenia:** lokalna lista kart Wi‑Fi/Ethernet (oraz Bluetooth PAN, jeśli Windows wystawia go jako adapter sieciowy); parowanie urządzeń Bluetooth nie jest jeszcze diagnozowane. Jedno zapytanie ICMP do `1.1.1.1` jest wykonywane wyłącznie po kliknięciu „Sprawdź ping”. Program nie resetuje adapterów, DNS ani innych ustawień sieci.
- **Propozycje:** otwierają odpowiednie strony Ustawień Windows; użytkownik sam decyduje o każdej zmianie. Blessed nie wyłącza usług, zabezpieczeń ani aktualizacji i nie usuwa plików.
- **Personalizacja:** motyw i akcent zmieniają wygląd aplikacji w bieżącej sesji. Systemowe ustawienia wyglądu nie są automatycznie modyfikowane.
- **Updater:** przy starcie sprawdza publiczne wydania GitHub. Przed pobraniem i podmianą aplikacji prosi o zgodę użytkownika, sprawdza sumę SHA-256 opublikowaną obok pliku (kontrola integralności, nie podpis wydawcy) i aktualizuje kopię w profilu użytkownika. Tryb przenośny nie podmienia sam siebie.

Instalator odczytuje parametry lokalnie, by pokazać dopasowane ciekawostki. Nie uruchamia benchmarku — pytanie o „300 kalkulatorów” jest żartem, a nie deklaracją wydajności. Nie ma telemetrii ani automatycznego skanowania w tle. Jedyną rutynową komunikacją sieciową jest sprawdzenie wydania GitHub; test ping wymaga osobnego kliknięcia.

## Pobieranie i wydanie

Przycisk na stronie sprawdza najnowsze publiczne wydanie przez GitHub API. Bezpośrednie pobieranie włącza się tylko wtedy, gdy wydanie zawiera `BlessedOptimizer-Setup.exe`; w przeciwnym razie przycisk prowadzi do listy Releases, zamiast do niedziałającego adresu i błędu 404. Workflow `.github/workflows/windows-release.yml` publikuje `BlessedOptimizer-Setup.exe`, `SHA256SUMS.txt` i manifest updatera po wypchnięciu tagu `vMAJOR.MINOR.PATCH` albo ręcznym uruchomieniu workflow z `main` i podaniem np. `v1.0.2`. Build uruchamia też na Windows read-only smoke checks dla API zasilania, enumeracji procesów i odczytu autostartu; nie zapisuje ustawień systemu. Pull request i zmiany na `main` udostępniają artefakt Actions, ale nie tworzą publicznego Release.

Build jest **Windows x64** i zawiera .NET, więc użytkownik nie musi instalować osobnego runtime’u. To jeden plik dla Windows 10/11 x64; Windows 11 na ARM może uruchomić go przez emulację x64, ale nie jest to natywny build ARM64. Plik nie jest podpisany certyfikatem code-signing — Windows SmartScreen może wyświetlić ostrzeżenie.

## Strona lokalnie

Bez zależności i kroku kompilacji. Otwórz `site/index.html` albo uruchom:

```bash
python -m http.server 4173 --bind 0.0.0.0 -d site
```

## Aplikacja lokalnie

Wymagany .NET 10 SDK i Windows. Publikacja zgodna z workflow:

```powershell
dotnet publish app/BlessedOptimizer/BlessedOptimizer.csproj `
  --configuration Release --runtime win-x64 --self-contained true `
  --output artifacts/publish `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true
```

Read-only smoke checks (Windows only):

```powershell
dotnet run --configuration Release --project app/BlessedOptimizer.SmokeTests/BlessedOptimizer.SmokeTests.csproj
```

They verify that Windows exposes the active plan, local process enumeration works, and the processor limit choices stay at 50–100%. They do not apply power or autostart changes; those write paths still need manual testing on a Windows PC with consent and UAC.

## GitHub Pages

Workflow `.github/workflows/pages.yml` sprawdza składnię JavaScript i lokalne zasoby strony w pull requestach. Po scaleniu zmiany do `main` automatycznie publikuje katalog `site/` przez GitHub Actions. Adres projektu będzie dostępny pod `https://rejson59.github.io/Blessed-Optimizer/`.

Przed pierwszą publikacją administrator repozytorium musi jednorazowo wybrać **Settings → Pages → Build and deployment → Source: GitHub Actions**. Sama strona używa ścieżek względnych, więc działa pod adresem projektu GitHub Pages.

## Struktura

- `site/` — strona i demonstracyjny symulator
- `app/BlessedOptimizer/` — natywna aplikacja WPF, instalator, diagnostyka, eksplorator procesów, autostart, ustawienia zasilania i updater
- `app/BlessedOptimizer.SmokeTests/` — read-only kontrole integracyjne uruchamiane na Windows w CI
- `.github/workflows/pages.yml` — publikacja GitHub Pages
- `.github/workflows/windows-release.yml` — build Windows x64 i publikacja assetów wydań
