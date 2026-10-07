# Blessed Optimizer · strona i aplikacja Windows v1.0.3

Blessed to pomocnik, który sam pilnuje Windowsa: co kilka minut przegląda komputer, wyłapuje rzeczy, o których system milczy, i proponuje gotowe rozwiązanie jednym kliknięciem.

Responsywna, polskojęzyczna strona projektu z osobnymi zakładkami „O programie” i „Symulator”, niebieskim gradientem, szklistymi panelami oraz gołębiem Blessed. Interaktywny podgląd odwzorowuje układ rzeczywistej aplikacji Windows; liczby na stronie są przykładowe, a pełne odczyty pokazuje program dla Windows.

W katalogu `app/BlessedOptimizer/` znajduje się pierwsza natywna aplikacja Windows. Jej numer wersji to **1.0.3**. GitHub Actions buduje ją jako jeden samodzielny plik `.exe`, a instalator trafia do użytkowników po opublikowaniu GitHub Release z tagiem `v1.0.3`.

## Co robi aplikacja v1.0.3

- **Blessed czuwa (nowe w 1.0.3):** domyślna karta aplikacji. Co 3 minuty w tle wykonuje pełny przegląd i pokazuje listę spraw posortowaną według wagi, każdą z gotowym działaniem („Zajmij się tym za mnie”). Sprawdza: wolne miejsce na dysku systemowym, zajętość RAM wraz z procesem, który zjada najwięcej, obciążenie procesora, liczbę aktywnych wpisów autostartu, czas pracy bez restartu, poziom i tryb baterii, limit procesora w aktywnym planie zasilania, status S.M.A.R.T. nośnika oraz objętość plików tymczasowych.
- **Rzeczy, o których Windows nie mówi:** monitor pracujący poniżej obsługiwanej częstotliwości odświeżania (porównanie trybów `EnumDisplaySettings`), przewidywana awaria dysku z `MSStorageDriver_FailurePredictStatus`, plan zasilania ograniczający procesor przy zasilaniu z sieci oraz gigabajty plików tymczasowych starszych niż dwa dni.
- **Automatyzacje Blessed:** sprzątanie `%TEMP%` (tylko pliki starsze niż 2 dni, pliki w użyciu pomijane) — ręcznie przyciskiem albo samodzielnie, jeśli użytkownik na to pozwoli i śmieci przekroczą 1 GB; nie częściej niż raz na 12 godzin. Suma zwolnionego miejsca jest widoczna w karcie opieki.
- **Priorytety użytkownika:** przy pierwszym uruchomieniu Blessed pyta, co jest ważne — granie, praca, bateria albo cisza — i pod to dobiera wnioski oraz kolejność spraw. W tej samej karcie użytkownik zaznacza, czym Blessed może zająć się sam (czuwanie w tle, sprzątanie plików tymczasowych, autostart, plan zasilania). Wybory zapisują się w `%LOCALAPPDATA%\BlessedOptimizer\blessed-profile.json`.
- **Interfejs:** pięć głównych kategorii — Strefa gracza, Połączenia, Propozycje i Personalizacja Windows — oraz osobna grupa dodatkowych narzędzi: Procesy, Autostart i Zasilanie. Układ odpowiada interaktywnemu podglądowi na stronie.
- **Instalator w tym samym pliku `.exe`:** instalacja tylko dla bieżącego użytkownika do `%LOCALAPPDATA%\Programs\BlessedOptimizer`, skrót w menu Start i opcjonalny skrót na pulpicie. Bez uprawnień administratora, autostartu ani automatycznego zamykania aplikacji.
- **Lokalny przegląd urządzenia:** Windows, nazwa procesora, liczba logicznych wątków, pamięć RAM, nazwa karty graficznej, wolne miejsce na dysku systemowym i widoczne karty sieciowe. Te odczyty nie są wysyłane do usługi Blessed.
- **Procesy:** tabela lokalnych procesów z PID, szacowanym użyciem CPU (z kolejnych próbek), pamięcią roboczą i czasem uruchomienia; wyszukiwanie po nazwie lub PID i odświeżanie co 2 sekundy tylko na otwartej karcie. To czysty podgląd zasobów, bez ingerencji w działające programy.
- **Strefa gracza:** po włączeniu pokazuje ogólne użycie CPU i RAM co dwie sekundy. Czuwanie zatrzymuje się po wyjściu z tej sekcji lub zamknięciu programu.
- **Autostart:** odczyt i zarządzanie wpisami `Run`/`RunOnce` bieżącego użytkownika. Przed wyłączeniem aplikacja pokazuje polecenie, pyta o zgodę i zachowuje kopię do przywrócenia. Autostart maszynowy, usługi i zadania systemowe pozostają nietknięte.
- **Zaawansowane zasilanie:** odczyt aktywnego planu Windows oraz dostępnych ustawień maksymalnego stanu procesora, oszczędzania energii USB i łącza PCIe — osobno dla zasilania z sieci i baterii. Zmiana wymaga potwierdzenia w aplikacji i osobnego monitowania UAC, zapisuje oryginalne wartości lokalnie i oferuje ich przywrócenie. Pomocnik odmawia zapisu, jeśli podczas UAC aktywny plan lub konto Windows zmieniły się. Wpływ na wydajność, energię i responsywność jest opisany przy każdej opcji; limity termiczne i zabezpieczenia pozostają nietknięte.
- **Połączenia:** lokalna lista kart Wi‑Fi/Ethernet (oraz Bluetooth PAN, jeśli Windows wystawia go jako adapter sieciowy). Jedno zapytanie ICMP do `1.1.1.1` wykonuje się po kliknięciu „Sprawdź ping”; ustawienia adapterów, DNS i TCP pozostają bez zmian.
- **Propozycje:** prowadzą prosto do właściwych stron Ustawień Windows; o każdej zmianie decyduje użytkownik. Usługi, zabezpieczenia, aktualizacje i pliki pozostają nietknięte.
- **Personalizacja:** motyw i akcent zmieniają wygląd aplikacji od razu. Systemowe ustawienia wyglądu użytkownik otwiera osobno.
- **Updater:** przy starcie sprawdza publiczne wydania GitHub. Przed pobraniem i podmianą prosi o zgodę, weryfikuje sumę SHA-256 opublikowaną obok pliku i aktualizuje kopię w profilu użytkownika. Tryb przenośny aktualizuje się ręcznie.

Instalator odczytuje parametry lokalnie, by pokazać dopasowane ciekawostki (pytanie o „300 kalkulatorów” to żart Blessed). Bez telemetrii i bez skanowania w tle — jedyną rutynową komunikacją sieciową jest sprawdzenie wydania GitHub, a test ping uruchamiasz kliknięciem.

## Pobieranie i wydanie

Przycisk na stronie sprawdza najnowsze publiczne wydanie przez GitHub API. Gdy wydanie zawiera `BlessedOptimizer-Setup.exe`, przycisk pobiera plik bezpośrednio; w pozostałych przypadkach otwiera listę Releases. Workflow `.github/workflows/windows-release.yml` publikuje `BlessedOptimizer-Setup.exe`, `SHA256SUMS.txt` i manifest updatera po wypchnięciu tagu `vMAJOR.MINOR.PATCH` albo ręcznym uruchomieniu workflow z `main` i podaniem np. `v1.0.3`. Build uruchamia też na Windows read-only smoke checks dla API zasilania, enumeracji procesów i odczytu autostartu; nie zapisuje ustawień systemu. Pull request i zmiany na `main` udostępniają artefakt Actions, ale nie tworzą publicznego Release.

Build jest **Windows x64** i zawiera .NET, więc użytkownik nie musi instalować osobnego runtime’u. To jeden plik dla Windows 10/11 x64; na Windows 11 ARM działa przez emulację x64.

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

They verify that Windows exposes the active plan, local process enumeration works, the processor limit choices stay at 50–100%, the refresh-rate probe returns a sane mode, the TEMP scan is non-negative and a full Blessed sweep returns findings. They do not apply power or autostart changes; those write paths still need manual testing on a Windows PC with consent and UAC.

## GitHub Pages

Workflow `.github/workflows/pages.yml` sprawdza składnię JavaScript i lokalne zasoby strony w pull requestach. Po scaleniu zmiany do `main` automatycznie publikuje katalog `site/` przez GitHub Actions. Adres projektu będzie dostępny pod `https://rejson59.github.io/Blessed-Optimizer/`.

Przed pierwszą publikacją administrator repozytorium musi jednorazowo wybrać **Settings → Pages → Build and deployment → Source: GitHub Actions**. Sama strona używa ścieżek względnych, więc działa pod adresem projektu GitHub Pages.

## Struktura

- `site/` — strona i demonstracyjny symulator
- `app/BlessedOptimizer/` — natywna aplikacja WPF: karta „Blessed czuwa” (automatyczny przegląd, profil priorytetów, sprzątanie plików tymczasowych), instalator, diagnostyka, eksplorator procesów, autostart, ustawienia zasilania i updater
- `app/BlessedOptimizer.SmokeTests/` — read-only kontrole integracyjne uruchamiane na Windows w CI
- `.github/workflows/pages.yml` — publikacja GitHub Pages
- `.github/workflows/windows-release.yml` — build Windows x64 i publikacja assetów wydań
