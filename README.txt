MINECRAFT × SPACE ENGINEERS — CREATIVE
Lokalna integracja v2 z ekonomią v3 dla Minecraft Java 26.3 i Space Engineers 1.210.014.

START
Otwórz skrót „Minecraft x Space Engineers” na pulpicie.
Minecraft uruchamia się w tle, a widocznym światem i sterowaniem zajmuje się SE.
Nie trzeba uruchamiać panelu ani wykonywać poleceń. Pierwsze wczytanie może chwilę potrwać.
Po grze zamknij SE normalnie z menu. Ukryty Minecraft po kilkunastu sekundach
sam zapisze swój świat i się wyłączy.

STEROWANIE
WASD / mysz / Spacja — zwykły ruch postaci SE.
J — plecak odrzutowy. F — wejście do kokpitu lub wyjście z niego.
LPM — usuń wskazany blok Minecrafta albo wykop fragment terenu planety/asteroidy SE.
PPM — postaw posiadany blok albo użyj wybranego przedmiotu MC.
E — użyj dźwigni, skrzyni lub stołu rzemieślniczego.
I — wspólne EQ i wytwarzanie. G — automatyczny katalog z wyszukiwaniem.
Shift+I — natywny ekwipunek i transfer do schowków SE; G otwiera wspólny katalog w trybie MC.
Kółko myszy — wybór posiadanych przedmiotów MC. 1–9 — pierwsze pozycje palety, jeśli je posiadasz.
Ctrl+Alt+B — przełącz budowanie Minecraft / zwykłe narzędzia i budowanie SE.
W kokpicie działają normalne sterowanie, hamulec i pasek narzędzi SE.

SKRZYNIE, WSPÓLNE EQ I CRAFTING
LPM przenosi stos, PPM dzieli lub odkłada jedną sztukę, Shift+LPM wykonuje szybki transfer.
Tak działa menu natywnej skrzyni MC. I pokazuje przedmioty z obu ekwipunków.
Zakładka Wytwarzanie wyszukuje receptury i pobiera rzeczywiste składniki z obu gier.
E przy stole MC otwiera ten sam wspólny warsztat. Katalog służy do wyszukiwania,
a wybór do ręki wymaga posiadania przedmiotu. Narzędzia SE przełączają sterowanie na SE.
Receptury MC są sprawdzane przez prawdziwy silnik, a receptury SE pochodzą z jego blueprintów.
Sztabki o zgodnym metalu są wymienne: 1 sztabka MC = 1 kg sztabki SE.
To jawna reguła tej piaskownicy, nie przeliczenie fizycznej masy przedmiotów MC.
Przerwana transakcja jest zapisywana i wznawiana po uruchomieniu obu gier.
Esc lub krzyżyk wraca do świata; przedmiot trzymany kursorem wraca do ekwipunku.

CO JEST POŁĄCZONE
• Tekstury i modele Minecrafta w świetle, perspektywie i świecie SE; blok ma jeden metr.
• Natywne kolizje SE: postać, koła, pancerz i narzędzia oddziałują z blokami MC.
• Budowanie i kopanie z SE zmienia prawdziwy zapis świata MC.
• Narzędzie MC kopie także natywny teren SE; nowe dno wykopu podpiera spadające bloki MC.
• Redstone, lampy, dźwignie, tłoki, skrzynie i crafting są obliczane przez Minecrafta.
• Ruchomy tłok MC ma kolizję w SE; potwierdzono przepchnięcie testowej bryły pancerza.
• Spadający piasek jest widoczny w SE, a jego lot i osiadanie oblicza Minecraft.
• Wiertła SE niszczą bloki MC; odpowiednio silne zderzenia mogą je rozbić.
• TNT ma prawdziwy lont MC, a wybuch uszkadza również pancerz i teren SE.
• Okno aktualizacji 64³ podąża za graczem. Obejrzane konstrukcje zachowują grafikę i kolizje poza nim.
• Odległe, załadowane przez MC fragmenty dostają dodatkowe aktualizacje; natywne niezaładowane chunki pozostają uśpione.
• Dodatkowy zapis odtwarza widziane konstrukcje i kolizje także przy wyłączonym zapleczu Minecrafta.

ŚWIAT STARTOWY
Na planecie stoi drewniany garaż ze szkłem, działającym światłem, skrzynią i craftingiem.
Do garażu prowadzi stalowa rampa SE, a w środku na podłodze MC stoi prawdziwy łazik SE.
Wjazd ma 7 m szerokości i 4 m wysokości. Można rozbudowywać obie części świata zwykłymi narzędziami.
Łazik startowy ma ustawiony limit prędkości 20 km/h.
Świat jest lokalną piaskownicą kreatywną, bez głodu i konieczności jedzenia.

POTWIERDZONE TESTY
Sprawdzono chodzenie i jazdę po blokach MC, tłoki, TNT, kopanie, piasek,
skrzynie i crafting, odległe aktualizacje oraz zapis i ponowny start obu gier.
Pełne wyniki, pomiary i ograniczenia weryfikacji: qa-live-v2.json.
Nowe testy katalogu, wspólnego EQ, mieszanych receptur, komponentów i odzyskiwania:
qa-live-v3.json oraz qa-live-v3-tests.json.

ZAKRES WERSJI
To instalacja dopasowana do tego komputera i tych wersji gier, w trybie jednoosobowym OFFLINE.
Katalog automatycznie obejmuje rejestry obu gier; nowe zwykłe bloki i przedmioty nie potrzebują ręcznej listy.
Wspólny warsztat obsługuje zwykłe receptury MC 3×3 i materialne blueprinty SE.
Na tym etapie warsztat nie wymaga fizycznego assemblera ani energii; nie zastępuje pełnego survivalu.
Piece, receptury specjalne i dalsze procesy przemysłowe wymagają adapterów całych rodzin.
Widoczność w katalogu nie oznacza kompletnej obsługi każdej mechaniki danego przedmiotu.
Kopanie terenu SE jest funkcją kreatywną i nie przyznaje rudy ani przedmiotów do ekwipunku MC.
Nie ma jeszcze pełnej obsługi wszystkich mobów, wymiarów Minecrafta, multiplayera ani całej ekonomii survivalowej.
Ruchome tłoki są próbkowane co 50 ms; bardzo krótki ruch przy opóźnionej klatce może być częściowo pominięty.
Konstrukcje Minecrafta są związane z planetarnym układem współrzędnych; nie stają się automatycznie statkami SE.
Zainstalowanych zasobów Minecrafta nie dołączaj do publicznej paczki z kodem moda.

PLIKI I ZAPISY
Minecraft: minecraft-backend\saves\SEMC Backend
Space Engineers: %APPDATA%\SpaceEngineers\Saves\76561198055314775\SEMC Lab - Bridge Test
Cache wizualizacji i kolizji: world-cache
Kopie wcześniejszych światów: backups
Źródła: minecraft-bridge\v2 oraz se-bridge\v2
Logi: mc-v2.log, se-v2.log, backend-launch-v2.log, bootstrap-v2.log

Stare SEMC-Lab.exe i dokumenty PROTOCOL.md / LIVE-TEST-2026-10-15.txt dotyczą poprzedniego eksperymentu.
Do gry używaj nowego skrótu i pliku Minecraft x Space Engineers.exe.
