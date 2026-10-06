# Blessed Optimizer · strona i aplikacja Windows v1.0.0

Responsywna, polskojęzyczna strona projektu z osobnymi zakładkami „O programie” i „Symulator”, niebieskim gradientem, szklistymi panelami oraz gołębiem Blessed. Strona zawiera koncepcyjną makietę aplikacji — nie wykonuje diagnostyki urządzenia.

W katalogu `app/BlessedOptimizer/` znajduje się pierwsza natywna aplikacja Windows. Jej numer wersji to **1.0.0**. Projekt jest przygotowany do zbudowania przez GitHub Actions jako jeden samodzielny plik `.exe`; publiczny plik do pobrania pojawi się w GitHub Releases po opublikowaniu tagu `v1.0.0`.

## Co robi aplikacja v1.0.0

- **Instalator w tym samym pliku `.exe`:** instalacja tylko dla bieżącego użytkownika do `%LOCALAPPDATA%\Programs\BlessedOptimizer`, skrót w menu Start i opcjonalny skrót na pulpicie. Bez uprawnień administratora, autostartu ani automatycznego zamykania aplikacji.
- **Lokalny, tylko do odczytu przegląd urządzenia:** Windows, nazwa procesora, liczba logicznych wątków, pamięć RAM, nazwa karty graficznej, wolne miejsce na dysku systemowym i widoczne karty sieciowe. Parametry nie są wysyłane do usługi Blessed.
- **Strefa gracza:** po włączeniu pokazuje ogólne użycie CPU i RAM co dwie sekundy. To nie jest pomiar FPS ani temperatury i nie identyfikuje procesów gry. Czuwanie zatrzymuje się po wyjściu z tej sekcji lub zamknięciu programu. Niczego nie zamyka.
- **Połączenia:** lokalna lista kart Wi‑Fi/Ethernet (oraz Bluetooth PAN, jeśli Windows wystawia go jako adapter sieciowy); parowanie urządzeń Bluetooth nie jest jeszcze diagnozowane. Jedno zapytanie ICMP do `1.1.1.1` jest wykonywane wyłącznie po kliknięciu „Sprawdź ping”. Program nie resetuje adapterów, DNS ani innych ustawień sieci.
- **Propozycje:** otwierają odpowiednie strony Ustawień Windows; użytkownik sam decyduje o każdej zmianie. Blessed nie stosuje optymalizacji ani nie usuwa plików.
- **Personalizacja:** motyw i akcent zmieniają wygląd aplikacji w bieżącej sesji. Systemowe ustawienia wyglądu nie są automatycznie modyfikowane.
- **Updater:** przy starcie sprawdza publiczne wydania GitHub. Przed pobraniem i podmianą aplikacji prosi o zgodę użytkownika, sprawdza sumę SHA-256 opublikowaną obok pliku (kontrola integralności, nie podpis wydawcy) i aktualizuje kopię w profilu użytkownika. Tryb przenośny nie podmienia sam siebie.

Instalator odczytuje parametry lokalnie, by pokazać dopasowane ciekawostki. Nie uruchamia benchmarku — pytanie o „300 kalkulatorów” jest żartem, a nie deklaracją wydajności. Nie ma telemetrii ani automatycznego skanowania w tle. Jedyną rutynową komunikacją sieciową jest sprawdzenie wydania GitHub; test ping wymaga osobnego kliknięcia.

## Pobieranie i wydanie

Przycisk pobierania na stronie prowadzi bezpośrednio do `https://github.com/rejson59/Blessed-Optimizer/releases/latest/download/BlessedOptimizer-Setup.exe`, więc przeglądarka pobiera najnowszy opublikowany instalator, a nie otwiera listy wydań. Ten stały adres zacznie działać po opublikowaniu pierwszego wydania. Workflow `.github/workflows/windows-release.yml` publikuje `BlessedOptimizer-Setup.exe`, `SHA256SUMS.txt` i manifest updatera po wypchnięciu tagu `vMAJOR.MINOR.PATCH` albo ręcznym uruchomieniu workflow z `main` i podaniem np. `v1.0.0`. Pull request i zmiany na `main` uruchamiają kompilację kontrolną oraz udostępniają artefakt Actions.

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

## GitHub Pages

Workflow `.github/workflows/pages.yml` sprawdza składnię JavaScript i lokalne zasoby strony w pull requestach. Po scaleniu zmiany do `main` automatycznie publikuje katalog `site/` przez GitHub Actions. Adres projektu będzie dostępny pod `https://rejson59.github.io/Blessed-Optimizer/`.

Przed pierwszą publikacją administrator repozytorium musi jednorazowo wybrać **Settings → Pages → Build and deployment → Source: GitHub Actions**. Sama strona używa ścieżek względnych, więc działa pod adresem projektu GitHub Pages.

## Struktura

- `site/` — strona i demonstracyjny symulator
- `app/BlessedOptimizer/` — natywna aplikacja WPF, instalator, diagnostyka, monitoring lokalny i updater
- `.github/workflows/pages.yml` — publikacja GitHub Pages
- `.github/workflows/windows-release.yml` — build Windows x64 i publikacja assetów wydań
