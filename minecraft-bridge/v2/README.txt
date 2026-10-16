SEMC: zaplecze Minecraft 26.3

Uruchamianie

silent-launch-v2.ps1 ponownie wykorzystuje dzialajacy, sprawdzony swiat
SEMC Backend. Jesli go nie ma, uruchamia lokalna sesje z zainstalowanego
Minecrafta 26.3 i Java 25. Parametr -StartSE uruchamia potem glowny
start-v2.ps1. Skrypt nie otwiera panelu laboratoryjnego.
Tryb -StartSE ustawia domyslnie wycinek64^3 i przesuwanie obszaru.
Sprawdza tozsamosc juz uruchomionego SE: PID+czas startu z pliku hosta albo
dokladna sciezke lokalnego Pulsara. Zwykla sesja SE powoduje komunikat
z prosba o zapisanie i zamkniecie gry przed ponownym uruchomieniem skrotu.

Pierwszy zimny start kopiuje tylko istniejacy klon SEMC Backend do katalogu
SEMC-Lab\minecraft-backend\saves\SEMC Backend. Zrodlowy klon musi byc wtedy
zamkniety. Oryginalne swiaty gracza nie sa kopiowane ani modyfikowane.
Kopiowanie ma blokade pliku session.lock i konczy sie zmiana nazwy katalogu
roboczego dopiero po skopiowaniu calego swiata. Nie ma automatycznego kasowania.

Zimny start uzywa lokalnej sesji offline: nazwy i UUID z zainstalowanego
profilu. Nie kopiuje tokenow, hasel ani danych logowania. Gra wieloosobowa
nie jest celem tego procesu. Zwykla sesja Minecrafta moze dzialac osobno;
skrypt nie zamyka jej swiata. Drugi proces tego samego zaplecza jest odrzucany.

Po przygotowaniu izolowanego katalogu dodawany jest profil odzyskiwania
SEMC Backend (Space Engineers) w oficjalnym launcherze. Istniejace profile
i wybor domyslny pozostaja zachowane; kopia konfiguracji trafia do backups\launcher.

Sprawdzenie bez uruchamiania lub zmian:
  .\silent-launch-v2.ps1 -ValidateOnly
  .\silent-launch-v2.ps1 -ValidateColdOnly

Stan, sterowanie i odzyskiwanie

backend-lifecycle-v2.json podaje PID, katalog gry, faze, przygotowanie,
ukrycie okna, wlaczenie streamingu i liczbe podporek terenu.
backend-launch-v2.log opisuje start. mc-v2.log opisuje dzialanie agenta.

  .\control.ps1 -Operation 8   pokazuje okno Minecrafta
  .\control.ps1 -Operation 7   ukrywa przygotowane zaplecze
  .\control.ps1 -Operation 4   zatrzymuje agenta i przywraca okno gry
  .\control.ps1 -Operation 11  zapisuje i oproznia bufor natywnego swiata
  .\control.ps1 -Operation 12  zapisuje swiat i lagodnie zamyka Minecrafta

Po pierwszym swiezym sygnale SE, brak kolejnego przez ponad15 sekund
powoduje sprawdzenie procesu SE z host-lifecycle-v2.json. Zarzadzane zaplecze
zapisuje swiat i zamyka sie dopiero po zakonczeniu tego procesu; czas startu
chroni przed ponownym uzyciem PID. Dlugie ladowanie swiata SE zachowuje backend.
Starszy host bez pliku tozsamosci stosuje poprzedni warunek15 sekund.
Agent podlaczony do zwyklego katalogu gracza jedynie przywraca okno.
Nie ma wymuszonego zabijania procesu.
Autostart pokazuje okno takze po przekroczeniu 180 sekund bez zaladowania
przygotowanego swiata. Reczny start agenta nie ukrywa innych swiatow.

Streamowanie i teren

Plik mc-v2-streaming.flag wlacza przesuwanie okna64^3 lub32^3. Aktualna pozycja
gracza z SE przesuwa okno krokami po8 blokow, z marginesem SIZE/4 od
krawedzi. Bloki poza oknem nadal istnieja w natywnym zapisie Minecrafta.
Zmiana okna nie wydaje polecen niszczenia blokow. Wylaczenie flagi pozostawia
ostatni obszar; polecenie3 moze ustawic inny poczatek.

terrain-v2.json tworzy podparcie z czterech warstw niewidocznych barrier,
odpowiadajace natywnym raycastom planety SE. Agent zapisuje wlasnosc tych
pol w terrain-proxy-owned-v2.json przed ich utworzeniem. Nie nadpisuje
blokow gracza; usuwa wylacznie swoje bariery w ponownie zmierzonych kolumnach.
Barier nie ma w obrazie eksportowanym do SE. Po zmianie terenu, np. wybuchu,
nowy pomiar aktualizuje natywna kolizje Minecrafta.

Potwierdzone na zywo

Native place/break; redstone block zapala lamp i usuniecie zrodla ja gasi;
orientacja pistona; ukrywanie, pokazywanie i przywracanie po utracie SE;
TNT: zaplon przez flint_and_steel, ruch encji, 80 tickow lontu, pojedyncze
zdarzenie po prawdziwym Level.explode i zniszczenie platformy Minecrafta;
4096 podporek terenu przy widocznym liczniku blokow0; paleta1592 stanow.

Test zimnego startu przeszedl 2026-10-15: natywny zapis i wyjscie zwolnily
session.lock, kopia klonu wystartowala w izolowanym katalogu, a zaplecze bylo
gotowe i ukryte po okolo13 sekundach. SE sam polaczyl sie ponownie. Zachowano
448 blokow garazu, zapalona lampe i4096 podporek. Pomiar:19.92 ticka/s przez
5.07s, sredni natywny tick1.08ms, odczyt wycinka0.90ms.

Osobny test64^3 takze przeszedl:262144 rekordy,448 widocznych blokow garazu,
16384 niewidocznych podporek,20.03 ticka/s przez10.036s, odczyt wycinka5.74ms.
Opcja64 i przesuwanie obszaru sa wlaczone. SE zachowuje odwiedzone fragmenty.
Oddzielny chunks-v2.bin aktualizuje zainteresowane fragmenty8^3, ktore sa
zaladowane w natywnym Minecraft. Test8 fragmentow:816 probek przez10.28s,
srednio0.158ms na partie4 fragmentow; niezmienione dane nie zajmowaly kolejnych
slotow pierscienia. Test SE usunal i odtworzyl blok garazu z odleglosci60m,
poza glownym wycinkiem, przez natywny BREAK i strumien zdalnych zmian.

Natywne kontenery: menu-v2.json opisuje okno, identyfikator, wspolrzedne
slotow, stosy i przedmiot na kursorze. Op13 wykonuje PICKUP lub QUICK_MOVE,
op14 zamyka okno. Test skrzyni przeniosl64 pnie do srodka i z powrotem.
Test stolu rzemieslniczego zuzyl1 pien z64 i wytworzyl4 deski; zamkniecie
z trzymanymi deskami zwrocilo je do ekwipunku. Powtorzone zamkniecie starego
identyfikatora zostalo odrzucone. Receptury i zuzycie sa natywne.

Plik mc-v2-capture64.flag wybiera64^3 przy uruchomieniu agenta; bez niego32^3.
Odczyt wymiarow w control.ps1 jest dynamiczny; obecna czesc SE obsluguje oba
rozmiary. Rekordy mieszcza sie w dotychczasowych8MiB.

world-v2.json zawiera trwale UUID swiata, wymiar i SHA256 pelnej natywnej
mapy stanow. UUID mieszka wewnatrz zapisu w semc-bridge-world.json.
Paleta zachowuje poznane identyfikatory po restarcie, odtwarzajac opisy z
natywnej wersji gry. saveCheckpoint rosnie po jawnym SAVE11 lub QUIT12;
zwykle autozapisy Minecrafta nie sa numerowane.

entities-v2.json eksportuje co50ms TNT, spadajace bloki i ruchome tloki.
Zawiera natywne polozenie, predkosc, postep tloka oraz dokladne biezace
ksztalty kolizji. Ruchoma glowica i nieruchoma podstawa maja osobne dane.
Test120 kombinacji kierunku/postepu zgodny z natywnymi ksztaltami; na zywo
zapisano wysuniecie i cofniecie lepkiego tloka oraz22 probki spadajacego
piasku. Osobny test hosta SE potwierdzil26 natywnych kontaktow tloka z bryla
27 blokow ciezkiego pancerza; bryla przesunela sie, zachowujac27 blokow.
Spadajacy piasek byl rowniez widoczny przez renderer SE i osiadl natywnie.
To wynik kontrolowanych testow; probkowanie50ms moze pominac czesc krotkiego
ruchu tloka, gdy klatka hosta jest opozniona.

Test offline cache22:23:33UTC: po SAVE11/QUIT12 i wyjsciu procesu MC,
SE odtworzyl8 fragmentow,463 komorki i74 kolidery z zapisu przy odczycie
MC przeterminowanym o13.5s. Postac zachowala podparcie MinecraftCollider.
Produktowy EXE uruchomil nastepnie zimny backend MC w10.7s, ukryl go i
wykorzystal dzialajace SE. UUID swiata i checkpoint2 zostaly zachowane.
Pelny zimny start obu gier przeszedl22:43:13–22:44:06UTC (okolo53s):
backend ukryty i gotowy po12s, natywne SE wczytalo swiat automatycznie.
UUID i checkpoint4 zachowane,448 blokow/45 koliderow/8 fragmentow,
zapalona lampa, wykop i zaparkowany lazik przetrwaly restart. Po natywnym
wyjsciu SE watchdog sam zapisal i zamknal backend. Dane obu silnikow
potwierdzaja wynik; narzedzie zrzutu ekranu po restarcie bylo niedostepne,
wiec nie deklarujemy nowej kontroli wizualnej po tym konkretnym starcie.

Ograniczenia

Rozmiar jednorazowego wycinka to64x64x64 (domyslnie32 bez flagi). Aktualny klon ma zwykly teren
Minecrafta ponizej obszaru w niebie; bardzo duze przesuniecie pionowe moze
go ujawnic. Ostateczny dedykowany swiat pusty wymaga osobnej migracji.
Podpory terenu sa schodkowe, co1m, i obejmuja tylko zmierzone kolumny.
Ich zgodnosc nie oznacza jeszcze podparcia na wszystkich dynamicznych
pojazdach/statkach SE. Backend eksportuje dane natywnych kontenerow; gra SE
rysuje wlasny ekran tych samych slotow. Przeciaganie rozdzielajace stos,
wyrzucanie poza menu i dodatkowe typy klikniec nie sa jeszcze obslugiwane.
