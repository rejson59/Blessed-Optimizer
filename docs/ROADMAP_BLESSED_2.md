# Plan rozwoju Blessed Optimizer 2.0

## Cel produktu

Blessed ma być lokalnym, zrozumiałym centrum diagnostyki i opieki nad Windows — nie „magicznym boosterem”. Powinien najpierw pokazać, co wykrył, wyjaśnić skutek, a dopiero potem zaproponować dobrowolną zmianę. Wynik zależy od podzespołów, sterowników, chłodzenia, zasilania i zastosowania; nie istnieje bezpieczny, uniwersalny przełącznik gwarantujący „100% możliwości” dla każdego PC.

## Co zostało zrobione w tej iteracji (kod 1.3.0)

- **Pierwsze błogosławieństwo** jest nowym ekranem startowym dla profili, które jeszcze go nie ukończyły. Po kliknięciu wykonuje krótki audyt tylko do odczytu i pokazuje wyniki w jednym miejscu.
- Audyt zbiera CPU/RAM, wolne miejsce na dysku systemowym, tryb głównego ekranu i odczytane częstotliwości, status obecnych urządzeń PnP, aktywny plan zasilania i stan kart sieciowych. Próbka CPU trwa sekundę i nie jest testem obciążeniowym ani benchmarkiem.
- Karta **Urządzenia** korzysta z SetupAPI z flagą `DIGCF_PRESENT`, więc nie pokazuje odłączonych, historycznych wpisów. Rozpoznaje monitory, klawiatury, myszy, kamery, dźwięk, kontrolery i część urządzeń obrazujących; status problemu pochodzi z Configuration Manager.
- Dla wykrytych urządzeń pokazujemy wyłącznie przyjazną nazwę, producenta, kategorię i kod stanu. Identyfikatory PnP nie są zapisywane. Nie otwieramy kamery/mikrofonu, nie odczytujemy klawiszy ani ruchu myszy i nie zmieniamy sterowników.
- Menu zostało uporządkowane: Pierwszy krok → Opieka → Wydajność → Urządzenia → Diagnostyka → Windows → Wygląd i Blessed.
- Testy sprawdzają klasyfikację urządzeń, poziomy ostrzeżeń audytu i trwałość daty ukończenia w lokalnym profilu. Windows smoke test tylko odczytuje listę obecnych urządzeń.

## Docelowy układ aplikacji 2.0

1. **Start / Pierwsze błogosławieństwo** — kontrolowany audyt, podsumowanie mocnych stron i lista rzeczy do sprawdzenia; osobny profil celu: granie, praca, bateria, cisza albo własny profil.
2. **Opieka** — alerty z poziomem pewności, wpływem, źródłem danych, datą i przyciskiem „dlaczego?”. Historia oraz możliwość wyciszenia/przywrócenia alertu.
3. **Wydajność** — CPU/RAM/procesy, plan zasilania i opcjonalne krótkie testy porównawcze uruchamiane wyłącznie na żądanie. Nie zamykać procesów automatycznie ani nie obiecywać wzrostu FPS.
4. **Urządzenia** — osobne sekcje: monitory, kamera/prywatność, audio, klawiatura i mysz, kontrolery, USB/Bluetooth, sieć, dyski i bateria. Każda sekcja rozróżnia „Windows wykrył”, „nie wykryto” i „odczyt niedostępny”.
5. **Diagnostyka** — raporty z czytelnym źródłem/zakresem oraz eksport do lokalnego pliku za wyraźną zgodą. Nie wysyłać logów automatycznie.
6. **Windows** — autostart, zasilanie, porządki i ustawienia systemu. Oddzielić odczyt, proponowaną zmianę, zatwierdzenie, zastosowanie i przywrócenie.
7. **Wygląd i Blessed** — motyw, akcent, dostępność, animacje, tryb ograniczonego ruchu, ustawienia maskotki i prywatność.

## Kolejne etapy

### 1.3.x — wiarygodna baza diagnostyczna

- Dodać obsługę wszystkich monitorów (nie tylko głównego): identyfikator tymczasowy na czas sesji, rozdzielczość, Hz i bezpieczny odnośnik do ustawień. Odczyt HDR/VRR tylko wtedy, gdy Windows udostępni wiarygodne API.
- Uzupełnić odczyt peryferiów o status dostępu do kamery z udokumentowanego API/ustawień Windows, poziomy/urządzenia audio, Bluetooth i kontrolery. Nigdy nie uruchamiać strumienia kamery ani mikrofonu w tle.
- Dodać filtry, sortowanie, odświeżanie i rozróżnienie „obecny”, „problem sterownika”, „brak danych”; nie zgadywać stanu urządzenia, gdy API jest niedostępne.
- Rozbudować testy integracyjne na prawdziwym Windows 10/11: laptop/desktop, wiele ekranów, brak kamery, odłączane USB, odmowa dostępu i profile bez uprawnień administratora.

### 1.4 — optymalizacja kontrolowana przez użytkownika

- Każda zmiana ma kartę planu: wartość przed/po, zakres (konto/plan urządzenia), możliwy skutek, ryzyko, potrzeba UAC, kopię oryginału i przycisk cofnięcia.
- „Zastosuj” działa pojedynczo lub jako krótki plan, ale wyłącznie po pokazaniu dokładnej listy zmian. Nigdy nie obejmuje usług Windows, Defendera, zapory, aktualizacji, sterowników, limitów termicznych ani nieznanych kluczy rejestru.
- Wprowadzić dziennik zmian lokalnych i kontrolę, czy bieżący plan/użytkownik/urządzenie nadal są tymi, dla których przygotowano plan.

### 2.0 — profile, historia i jakość

- Profile zależne od kontekstu („praca”, „gra”, „mobilny”), ale bez automatycznego przełączania ustawień systemowych bez zgody.
- Porównywanie przed/po przy tych samych warunkach. Wyniki oznaczać jako obserwację, nie gwarancję; pokazać zmienność i ograniczenia pomiaru.
- Opcjonalny eksport raportu diagnostycznego do pliku i możliwość jego podglądu przed zapisaniem.
- Podpisywanie binariów, aktualizacje przez sprawdzone kanały i testy regresji każdego modułu zapisującego ustawienia.

## Zasady bezpieczeństwa i wiarygodności

- Odczyt nie zmienia stanu; test obciążeniowy nie jest uruchamiany w tle.
- Nie da się przetestować „wszystkich możliwych ustawień” bez ich faktycznego zastosowania. Dlatego Pierwsze błogosławieństwo enumeruje to, co Windows już raportuje, i oznacza sugestie jako opcjonalne.
- Żaden brak wykrytego urządzenia nie jest automatycznie awarią. Urządzenia wirtualne, docki, sterowniki OEM i uprawnienia potrafią ograniczyć widoczność.
- Kamera i mikrofon pozostają prywatne; aplikacja nie odczytuje treści wejścia, nie wyłącza zabezpieczeń, nie czyści rejestru, nie usuwa komponentów systemowych ani nie obiecuje uniwersalnego wzrostu wydajności.
- Dane urządzenia i historii zostają lokalnie. Telemetria nie jest wymagana; każda diagnostyka eksportowana poza komputer wymaga osobnej zgody.
- Przed każdą operacją zapisu wymagane są testy na obsługiwanych wersjach Windows i ręczny test cofnięcia na urządzeniu testowym.

## Kryteria ukończenia funkcji

- Funkcja jest dostępna z właściwej kategorii nawigacji, działa bez administratora, opisuje źródło i granice odczytu.
- Każdy brak danych i błąd API ma bezpieczny stan „niedostępne”, bez fałszywej porady.
- Test jednostkowy obejmuje regułę; smoke test urządzenia jest tylko do odczytu; zapis systemowy ma osobny test, potwierdzenie, kopię i rollback.
- README i strona symulatora odzwierciedlają to, co rzeczywiście robi aplikacja — nie makietę jako funkcję już działającą.
