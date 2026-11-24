# SubtitleForge – Jellyfin-Plugin für Untertitel

Eigenständiges Plugin für Jellyfin 12.x (.NET 10). Es braucht keine externen Dienste wie Sonarr, Radarr oder Bazarr und läuft auf der CPU (also auch auf NAS-Geräten wie dem Intel N100), Whisper optional auf der GPU per Vulkan.

## Funktionen

| Funktion | Wie |
|---|---|
| **Einheitlicher Untertitel-Stil** | Schrift, Größe, Farbe, Rand, Hintergrund und Position werden vom Server vorgegeben und im Web-Client und in Jellyfin Desktop erzwungen (Skript-Injection in `index.html`, ohne Dateien zu ändern). Für Android TV und Handy-Apps schreibt das Plugin bereinigte SRT-Dateien, damit dort die App-Einstellungen einheitlich greifen. |
| **Bild-Untertitel → Text (OCR)** | Geplante Aufgabe. Eingebettete PGS-Untertitel (Blu-ray) werden per eigenem PGS-Decoder und PaddleOCR (PP-OCRv5 Latin, ONNX) einmalig in `Film.deu.OCR.srt` umgewandelt. |
| **Fehlende Untertitel herunterladen** | Geplante Aufgabe sowie Eintrag „SubtitleForge“ in Jellyfins Untertitelsuche. Quellen sind SubDL, OpenSubtitles.com und Jimaku (Anime, Japanisch/Englisch, eigener API-Key) (OpenSubtitles automatisch nur bei Hash-Treffer, um das Tageslimit zu schonen). Die Kandidaten werden danach bewertet, wie gut sie zum Release passen (Gruppe, Quelle, Dienst, Auflösung, Fassung, Folge). |
| **Sprachen pro Film/Serie/Bibliothek** | Tag `subs:de,ja` im Metadaten-Editor (an der Serie gilt er für alle Folgen, `subs:none` schaltet ab) → Bibliothekseinstellung „Untertitel herunterladen → Sprachen“ → Plugin-Standard. |
| **Nur gesprochener Text** | Entfernt Geräusch-/Musikbeschreibungen ([SCREAMING], (Schüsse), auch über zwei Zeilen) und Sprechernamen („JOHN:“) aus allen Untertiteln, die das Plugin schreibt; Liedtexte (♪) optional. |
| **Ganze Bibliothek über Tage** | Ist das Tageslimit einer Quelle (z.B. 20 OpenSubtitles-Downloads) erreicht, pausiert das Plugin nur diese Quelle bis zum Reset; betroffene Einträge bleiben offen und werden in der nächsten Nacht fortgesetzt, bis die Bibliothek durch ist. |
| **Forced-Erkennung** | Forced-Untertitel (nur Schilder/Fremdsprache) werden am Release-Namen und an der geringen Abdeckung der Dialoge erkannt und als `.forced` gespeichert, nie als voller Untertitel. |
| **GPU-Beschleunigung (Vulkan)** | Whisper rechnet auf AMD-, Intel- (z.B. Arc A310, N100-iGPU) und NVIDIA-GPUs, sobald eine GPU unter `/dev/dri` sichtbar ist und `libvulkan1` + `mesa-vulkan-drivers` installiert sind; sonst automatisch CPU. Mesas Software-Vulkan (llvmpipe) wird bewusst nie verwendet. Status auf der Einstellungsseite. |
| **Spracherkennung (Whisper)** | Ergebnis heißt in Jellyfin „[AI] Whisper“ (bzw. „[AI] Whisper ja-de“ bei Übersetzung), damit maschinell erzeugter Text erkennbar ist. Erzeugt Untertitel aus einer Tonspur (CPU, whisper.cpp): manuell pro Film/Folge mit frei wählbarer Tonspur und Zielsprache. Automatisch als letzter Ausweg nur für Serien/Filme mit dem Tag `whisper:auto` (oder global eingeschaltet; `whisper:off` schließt aus). Optional ersetzt das Ergebnis einen heruntergeladenen Untertitel, der dann nie wieder geladen wird. |
| **Offline-Übersetzung** | Weicht die Tonspur-Sprache von der Zielsprache ab, wird transkribiert und lokal übersetzt: Deutsch↔Englisch (Opus-MT), Japanisch↔Englisch (FuguMT), Japanisch↔Deutsch über Englisch. Ohne passende Tonspur nimmt das Plugin automatisch die am besten erkannte (z.B. deutschen Dub vor Japanisch). Qualität: gut für Deutsch↔Englisch, für Japanisch eher zum Verstehen. |
| **Button auf der Detailseite** | Administratoren sehen auf der Seite eines Films, einer Folge, Staffel oder Serie den Button „Untertitel“ (CC-Symbol): neu suchen oder Whisper starten – für eine Staffel/Serie für alle Folgen auf einmal (Tonspur per Sprache wählbar). |
| **Vorschau und „passt nicht“** | In der Übersicht zeigt „Untertitel ansehen“ jede externe Datei mit Herkunft und Textprobe. Ein heruntergeladener Untertitel lässt sich verwerfen: er wird gelöscht, für diesen Titel gesperrt und neu gesucht. |
| **Statistik** | Abdeckung pro Bibliothek und Sprache (z. B. „Serien: 92 % Deutsch“) und wie viel davon vom Plugin stammt. |
| **Sync-Bericht** | Geplante Aufgabe „Sync-Bericht erstellen (nur prüfen)“ prüft alle externen SRT-Dateien gegen die Tonspur, ohne etwas zu ändern. Verschobene lassen sich einzeln korrigieren – fremde Dateien bekommen eine korrigierte Kopie („Sync“), das Original bleibt. |
| **Rücksicht auf Zuschauer** | Schwere Arbeit pausiert, solange jemand etwas schaut (ein laufender Whisper-Job wird angehalten und fortgesetzt). Automatische Arbeit optional nur in einem Zeitfenster (z. B. 1–7 Uhr). |
| **Benachrichtigung** | Nach jedem Lauf eine Zusammenfassung im Jellyfin-Aktivitätsprotokoll, optional per Webhook (ntfy, Gotify, Discord, JSON). |
| **Whisper als letzter Ausweg** | Optional: Folgen, für die seit X Tagen kein Untertitel gefunden wurde, bekommen nach der nächtlichen Download-Aufgabe einen Whisper-Untertitel – höchstens N pro Lauf, älteste Lücken zuerst. |
| **Absturzsicher** | Whisper läuft in einem eigenen Prozess: stürzt der GPU-Treiber ab, läuft Jellyfin weiter und der Job wird auf der CPU wiederholt. Ein kurzer GPU-Selbsttest nach dem Start (und per Button) zeigt Treiberprobleme sofort an. |
| **ASS/SSA → SRT** | Optional: eingebettete ASS-Untertitel (Anime) zusätzlich als sauberes SRT („Text“, Dialog + Lieder), damit der einheitliche Stil greift. |
| **Forced für Synchronfassungen** | Optional: gibt es eine Tonspur in der gewünschten Sprache, aber keinen Forced-Untertitel, entsteht einer aus den Schildern/Liedern der ASS-Spur oder wird als „nur fremdsprachige Teile“ geladen (nur bei exaktem Treffer). Mehrere OCR-Spuren einer Sprache: die mit nur einem Bruchteil der Einträge wird als forced erkannt. |
| **Neue Einträge sofort** | Neu hinzugefügte Filme und Folgen werden automatisch bearbeitet, sobald der Bibliotheks-Scan ruhig ist. |
| **Übersicht** | Die Plugin-Seite zeigt pro Film/Folge, welche Sprachen vorhanden sind oder fehlen und warum (nichts gefunden, verworfen wegen Asynchronität …), mit den Aktionen „Neu suchen“ und „Whisper“. |
| **Synchronität prüfen und korrigieren** | Silero VAD erkennt Sprache in der Tonspur (bei Surround-Ton im Center-Kanal). Der Abgleich korrigiert Versatz, Framerate (23,976/24/25) und Schnittunterschiede, sprachunabhängig. Untertitel, die nicht zur Tonspur passen, werden verworfen; bereits synchrone Untertitel bleiben unverändert. |

## Geplante Aufgaben (Dashboard → Geplante Aufgaben → SubtitleForge)

| Aufgabe | Standard-Zeitplan |
|---|---|
| Bild-Untertitel in Text umwandeln (OCR) | täglich 03:00 |
| Fehlende Untertitel herunterladen (Whisper-Fallback nur, wenn aktiviert) | täglich 04:00 |
| Untertitel-Synchronität prüfen und korrigieren | nur manuell |
| Sync-Bericht erstellen (nur prüfen) | nur manuell |

## GPU-Beschleunigung einrichten

- Native Installation (Debian/Ubuntu): `sudo apt install libvulkan1 mesa-vulkan-drivers`; der Jellyfin-Benutzer braucht Zugriff auf `/dev/dri` (Gruppen `render`/`video`, wie für Hardware-Transcoding). Danach Jellyfin neu starten.
- Docker: das offizielle Image enthält kein Vulkan. `/dev/dri` durchreichen und die beiden Pakete im Image nachinstallieren (eigenes Dockerfile).
- Die Vulkan-Variante von whisper.cpp baut `tools/build-whisper-vulkan.sh` (gleicher Stand wie Whisper.net 1.8.1).

## Installation über das Plugin-Repository

1. Jellyfin → Dashboard → Plugins → Repositorys → „+“ und als URL eintragen:
   `https://raw.githubusercontent.com/<owner>/<repo>/main/manifest.json`
2. Im Katalog „SubtitleForge“ installieren und Jellyfin neu starten.
3. Unter Plugins → SubtitleForge → Einstellungen Bibliotheken, Sprachen und API-Keys eintragen.

Releases entstehen automatisch: ein Tag `v<version>` (z. B. `v0.3.0.0`) baut das Plugin (`tools/package.sh`), hängt die Zip-Datei an ein GitHub-Release und trägt die Version in `manifest.json` ein (`tools/update-manifest.py`).

## Bauen

```bash
dotnet publish Jellyfin.Plugin.SubtitleForge/Jellyfin.Plugin.SubtitleForge.csproj -c Release -o out
```

Den Inhalt von `out/` (DLLs, `runtimes/` und `whisperhost/`) nach `<jellyfin-config>/plugins/SubtitleForge/` kopieren und Jellyfin neu starten. Ohne lokales .NET SDK baut `dev-deploy.sh` das Plugin in einem Podman-Container und spielt es in eine lokale Test-Instanz ein.

## Tests

```bash
dotnet test Jellyfin.Plugin.SubtitleForge.Tests
```

- Die Sync-Tests nutzen einen synthetischen Film mit Sprachausgabe (`tools/make_synth_film.py` erzeugt `Fixtures/synth/film.wav`).
- `SF_REAL_DIR` und `SF_ANIME_DIR` aktivieren die Kalibrierungstests mit echten Tonspuren und Referenz-Untertiteln (siehe `RealMediaSyncTests.cs` und `AnimeSyncTests.cs`).
- `SF_DUMP_PGS=<datei.sup>` exportiert die PGS-Bilder und das OCR-Ergebnis zur Fehlersuche.

## Lizenz

GPLv3 (siehe `LICENSE`). Fremdkomponenten: siehe `THIRD-PARTY-NOTICES.md`.
