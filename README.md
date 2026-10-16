# Minecraft × Space Engineers

Lokalna integracja obu silników: Space Engineers wyświetla świat i obsługuje gracza oraz fizykę, a ukryty Minecraft oblicza bloki, redstone, crafting i pozostałe obsługiwane mechaniki.

Obecny stan to grywalna piaskownica kreatywna dla **Minecraft Java 26.3 / Space Engineers 1.210.014 na Windows**. Repozytorium przechowuje kod istniejącej instalacji i historię dalszych prac; nie jest jeszcze uniwersalnym instalatorem na dowolny komputer.

## Co działa

- Budowanie bloków Minecrafta w świecie SE, oryginalne modele i tekstury, kolizje postaci i pojazdów.
- Redstone, światła, skrzynie, crafting i spadający piasek obsługiwane przez Minecrafta.
- Tłoki MC przepychające obiekty SE; TNT uszkadzające również pancerz i teren SE.
- Wiertła SE niszczące bloki MC oraz kreatywne kopanie natywnego terenu SE.
- Uruchamianie obu silników jednym skrótem, ukryte zaplecze MC, zapis i ponowne wczytywanie.
- Automatyczny katalog rejestrów obu gier, wspólny widok ekwipunku i warsztat korzystający z natywnych receptur.
- Mieszane składniki: odpowiadające sobie sztabki metali z MC i SE mogą wspólnie pokrywać koszt receptury. Katalog nie rozdaje przedmiotów; budowanie i crafting zużywają posiadane zasoby.

Instrukcja uruchamiania, sterowanie i ograniczenia: [README.txt](README.txt).
Wyniki wykonanych testów w grze: [qa-live-v2.json](qa-live-v2.json). To zapis ręcznej weryfikacji, nie automatyczny zestaw testów.
Architektura wspólnego EQ, zakres receptur i zasady przeliczania materiałów: [ECONOMY-V3.md](ECONOMY-V3.md).
Testy ekonomii: [scenariusze wykonane w obu grach](qa-live-v3-tests.json) i [weryfikacja integracji, restartu oraz interfejsu](qa-live-v3.json).

## Mapa kodu

| Ścieżka | Zawartość |
| --- | --- |
| `minecraft-bridge/v2/` | Agent Java, logika zaplecza i strumień danych z Minecrafta |
| `se-bridge/v2/` | Integracja SE: grafika, fizyka, sterowanie i interfejs |
| `Launcher.cs`, `Launch.ps1`, `start-v2.ps1` | Uruchamianie lokalnej instalacji |
| `render-assets/` | Skrypty przygotowania zasobów z posiadanych instalacji gier |
| `research-se/`, `research-mc/` | Narzędzia diagnostyczne i sprawdzające |
| [PROTOCOL-V2.md](PROTOCOL-V2.md), [CHUNKS-V2.md](CHUNKS-V2.md) | Dokumentacja komunikacji między silnikami |

Starsze pliki poza katalogami `v2` i `control-panel/` dokumentują pierwszy eksperyment. Aktywna wersja nie wymaga panelu Lab.

## Praca nad kodem

Budowanie odbywa się lokalnie przez `minecraft-bridge/v2/build.ps1` oraz `se-bridge/v2/build.ps1`. Skrypty korzystają z zainstalowanych gier, JDK, bibliotek Pulsar i lokalnych ścieżek; przed użyciem na innym komputerze trzeba je dostosować. Nie ma chmurowego CI wymagającego plików gier.

Testy bez uruchamiania świata: `minecraft-bridge/v2/test-economy.ps1` i `research-se/test-economy.ps1`. Scenariusze `research-mc/test-economy-live-v3.ps1` wymagają uruchomionych dedykowanych światów i jawnie dodają testowe materiały; nie uruchamiaj ich jako zwykłego sprawdzenia kodu.

W repozytorium nie ma zapisów światów, wyciągniętych tekstur i modeli, plików binarnych gier, poświadczeń ani wygenerowanych plików komunikacji. Pozostają w lokalnej instalacji. Informacje o pochodzeniu zasobów: [ASSET-PROVENANCE.txt](render-assets/ASSET-PROVENANCE.txt).

## Dalszy kierunek

Katalog i receptury są odczytywane z rejestrów, a wspólny warsztat korzysta z ogólnych reguł materiałowych. Nowe przedmioty należące do obsługiwanych rodzin nie wymagają osobnego wpisu. Mechaniki takie jak piece, rafinerie, energia, płyny i receptury specjalne potrzebują kolejnych adapterów całych rodzin. Silniki nadal przechowują swoje natywne stosy z ich danymi; wspólny interfejs i transakcje łączą je podczas gry.

Projekt utrzymujemy prosto: jedna główna gałąź, zwykłe commity i aktualizacja instrukcji wtedy, gdy zmienia się sposób grania lub uruchamiania. Bez osobnej tablicy projektowej, harmonogramów i dodatkowych usług.
