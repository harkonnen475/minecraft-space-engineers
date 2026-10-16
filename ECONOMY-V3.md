# Katalog, wspólne EQ i crafting

Wersja 3 udostępnia w Space Engineers jeden ekran ekwipunku i wytwarzania. Widzi prawdziwe przedmioty postaci z obu gier, a receptury mogą pobierać materiały z obu zapasów. Minecraft nadal działa w tle. Nowych zwykłych przedmiotów i receptur nie dopisujemy do ręcznej listy.

## Co jest automatyczne

- Minecraft eksportuje swój aktywny rejestr przedmiotów, nazwy, modele, identyfikatory bloków, tagi i receptury. Dotyczy to również dodatkowych przestrzeni nazw z zainstalowanych modów.
- SE udostępnia katalog aktywnych definicji przedmiotów fizycznych oraz blueprintów. Import wykorzystuje rzeczywiste składniki, ilości i wyniki tych definicji.
- Zwykłe receptury Minecrafta z układem lub bez układu korzystają z natywnego sprawdzania dopasowania, tworzenia wyniku i zwracania pozostałości. Kontener po składniku, dane przedmiotu czy wariant wyniku rozstrzyga Minecraft.
- Partie mają osobną siatkę składników dla każdego powtórzenia. Przykładowo dwa osobne stosy po jednej kłodzie wystarczają do dwukrotnego wytworzenia desek; komponenty obu stosów pozostają rozróżnione.
- SE korzysta z zaimportowanych blueprintów, bez osobnej listy receptur naszego moda. Obecny wspólny warsztat wykonuje je bez kolejki maszyny, czasu produkcji i zapotrzebowania na energię.
- Złożone blueprinty SE są oznaczone jako „Komponenty: …”. Wytwarzają wymieniony zestaw części potrzebnych do budowy bloku, np. anteny; nie tworzą gotowej anteny jako przedmiotu w plecaku. Rozpoznanie odbywa się przez typ definicji, bez listy wyjątków dla poszczególnych bloków.
- Narzędzia SE dostępne do trzymania w ręce są rozpoznawane przez natywne definicje narzędzi. Wybór ze wspólnego EQ wyposaża konkretny posiadany przedmiot, zamiast tworzyć kopię.

Wykrycie nowej zawartości oznacza jej obecność w katalogu i możliwość używania przez już obsługiwane rodziny mechanik. Nie oznacza automatycznego przeniesienia dowolnego nowego zachowania, np. maszyny z niestandardowym kodem.

## Jedna obsługa, rodzime dane

Ekran łączy plecak Minecrafta i ekwipunek postaci SE. Każdy stos ma jednego właściciela: rodzimy silnik. Przedmioty MC zachowują natywne komponenty, zużycie, nazwy i inne dane. Przedmioty SE pozostają prawdziwymi przedmiotami SE. Nie tworzymy tysięcy atrap MC w definicjach SE ani dwóch niezależnych kopii tego samego stosu.

Receptura najpierw wykorzystuje własny materiał, a niedobór może pokryć zgodnym materiałem drugiej gry. Blueprint SE może więc zużyć minecraftowe sztabki; minecraftowa receptura może zużyć sztabki SE. Wytwarzanie zawsze pobiera składniki, także w obecnym świecie kreatywnym.

Na razie pozostają rodzime ograniczenia pojemności: sloty plecaka MC oraz masa i objętość ekwipunku SE. Wynik receptury MC trafia do plecaka MC, wynik blueprintu SE do plecaka SE; oba są widoczne w tym samym ekranie. To nie jest jeszcze wspólny system wszystkich skrzyń, kontenerów i przenośników.

## Reguła materiałów

Automatyczny adapter rozpoznaje przedmioty MC o nazwie `*_ingot` w dowolnej przestrzeni nazw oraz odpowiadający im podtyp `Ingot` w SE. Porównanie nazwy metalu pomija wielkość liter i podkreślenia. Obowiązuje jawny przelicznik:

**1 sztabka MC = 1 kg sztabki SE tego samego metalu.**

Powiązanie powstaje tylko wtedy, gdy obie strony mają po jednym jednoznacznym kandydacie. Dwa konkurujące przedmioty moda nie wygrywają kolejnością ładowania; niejednoznaczne powiązanie zostaje wyłączone. Ruda nie staje się automatycznie sztabką, a drewno nie staje się stalą.

Minecraftowe stosy są całkowite, a SE dopuszcza ułamki kilogramów. Jeśli receptura SE potrzebuje 0,25 kg, zużycie jednej sztabki MC oddaje 0,75 kg tej samej sztabki SE jako resztę. Materiał nie znika przez zaokrąglenie.

Uszkodzone lub niestandardowe przedmioty SE nie są automatycznie zużywane jako ich zwykłe odpowiedniki. Takie rozszerzenie wymaga reguły zachowania ich danych. Natywne składniki MC są przekazywane do Minecrafta z pełnymi komponentami.

## Bezpieczne wytwarzanie między silnikami

Przebieg pojedynczej operacji:

1. Planer sprawdza składniki, alternatywy, jednostki i docelową pojemność. Nie zmienia ekwipunków podczas planowania.
2. SE zapisuje dziennik z identyfikatorem operacji, tożsamością obu światów oraz oczekiwanymi stanami zapasu przed rezerwacją, po rezerwacji i po wydaniu wyniku.
3. SE rezerwuje własne składniki i zapisuje świat natywną, synchroniczną metodą zapisu.
4. Minecraft sprawdza dokładne sloty i skróty ich danych, rezerwuje składniki i zapisuje stan przygotowanej transakcji razem z ekwipunkiem.
5. Minecraft zatwierdza operację. Powtórzone żądanie z tym samym identyfikatorem zwraca istniejące potwierdzenie.
6. Dopiero po potwierdzeniu MC wydawane są ewentualne wyniki SE i zapisywany jest świat SE. Zakończona operacja dostaje trwałe potwierdzenie.

Przeładowanie kodu lub ponowne uruchomienie odczytuje niedokończony dziennik. Operacja sprawdza stan zapasu, zanim cokolwiek ponownie pobierze lub wyda. Niejednoznaczny stan zatrzymuje operację do odzyskania; nie zgaduje i nie wydaje drugiego wyniku. Nie ma ślepego zwrotu materiałów po nieznanym wyniku zdalnego zatwierdzenia.

Polecenia wymagają aktualnego połączenia z zarządzanym światem MC, zgodnego identyfikatora świata i wymiaru Overworld. Dziennik jest także związany z zapisem SE i konkretną postacią. Podczas operacji interakcje mostka, które mogłyby zmienić jej zapasy, są zablokowane. Zewnętrzna zmiana odpowiednich zapasów SE może zatrzymać transakcję do sprawdzenia, a brak miejsca po pasywnym podniesieniu przedmiotów może opóźnić wydanie wyniku.

## Pliki i interfejsy

`catalog-mc-v3.json` i `recipes-mc-v3.json` są generowanymi katalogami. `economy-state-v3.json` zawiera aktualne sloty, komponenty, ich skróty i wybrany slot MC.

Lokalny protokół wykorzystuje pliki `economy-request-v3.json` oraz `economy-response-v3.json`. Koperta ma `version`, `id`, `worldId`, `operation` i `args`. Operacje transakcyjne to `prepare`, `commit`, `abort` i `status`. Żądania przygotowania zawierają `txId`, konkretne sloty w `takes` oraz recepturę i siatki w `craft.grids`. Obce składniki w siatce są finansowane przez zarezerwowany materiał SE.

SE trzyma bieżący dziennik w `economy-se-v3/current.json`, a potwierdzenia w `economy-se-v3/receipts/`. MC trzyma swoje dzienniki przy rodzimym zapisie świata. Są to dane lokalnej sesji; nie należy publikować ich jako kodu repozytorium ani ręcznie kasować podczas niezakończonej operacji.

Główne moduły:

- `ItemCatalog.cs`: import i jednoznaczne reguły materiałów.
- `RecipeBridge.cs`: planer bez skutków ubocznych, alternatywy składników, partie i ułamkowe reszty.
- `UnifiedInventory.cs`: wspólny widok, wybór przedmiotów, dziennik, zapis i protokół transakcyjny.
- `UnifiedInventoryScreen.cs`: rodzimy ekran SE.
- `Economy.java`: natywny katalog, sloty, receptury i transakcje MC.

Widok katalogu i opisy receptur są buforowane. Aktualizacja zapasu odświeża ilości i dostępność, a dokładny planer jest uruchamiany dla receptur, które przejdą wstępne sprawdzenie dostępnych składników. Zatwierdzenie zawsze ponownie uruchamia dokładny planer.

## Zakres tej wersji

Obsługiwane są plecaki obu postaci, zwykły crafting MC i blueprinty przedmiotów SE we wspólnym przenośnym warsztacie. Katalog zachowuje także inne rodziny receptur, ale interfejs oznacza je jako wymagające osobnego urządzenia.

Nie ma jeszcze wspólnej sieci energii, przemysłowych kolejek produkcyjnych, logistyki między wszystkimi magazynami, pełnej obsługi pieców i kowalstwa przez ten warsztat, multiplayera ani gotowego balansu survivalowego. Import nowego przedmiotu jest automatyczny; nowa rodzina zachowania wymaga adaptera tej rodziny.

Transfer do natywnych schowków SE pozostaje dostępny przez Shift+I, a skrzynie MC przez E. Wydobycie bloków MC przez wiertło SE wymaga jeszcze osobnej reguły kwalifikacji narzędzia; obecne wyliczenie urobku wykorzystuje trzymane narzędzie MC.

## Sprawdzanie

`research-se/test-economy.ps1` kompiluje moduły i uruchamia `EconomyPlannerCheck.cs`. Sprawdza między innymi obie strony wymiany materiałów, mieszane zapasy, zachowanie komponentów, odmowę bez składników, ułamkowe reszty, alternatywy składników, partie rozłożone na różne stosy oraz jednoznaczne i niejednoznaczne materiały nowych przestrzeni nazw. Test nie uruchamia ani nie modyfikuje świata gry.

Diagnostyka istniejącego `semc-control.ps1` używa `-Action economy` i argumentów `operation`: `inspect`, `search`, `craft`, `select`, `export`. `pause-next` zatrzymuje w pamięci wskazany etap następnej operacji, aby sprawdzić odtwarzanie po przeładowaniu. Pauza znika przy przeładowaniu kodu, dziennik pozostaje. `grant-se` jest jawnym narzędziem testowym dodającym prawdziwy zapas; nie stanowi części normalnego wytwarzania.
