# SubtitleForge – Jellyfin-Plugin für Untertitel

Eigenständiges Plugin für Jellyfin 10.11 (.NET 9). Es braucht keine externen Dienste wie Sonarr, Radarr oder Bazarr und läuft rein auf der CPU, also auch auf NAS-Geräten (z.B. Intel N100).

## Funktionen

| Funktion | Wie |
|---|---|
| **Einheitlicher Untertitel-Stil** | Schrift, Größe, Farbe, Rand, Hintergrund und Position werden vom Server vorgegeben und im Web-Client und in Jellyfin Desktop erzwungen (Skript-Injection in `index.html`, ohne Dateien zu ändern). Für Android TV und Handy-Apps schreibt das Plugin bereinigte SRT-Dateien, damit dort die App-Einstellungen einheitlich greifen. |
| **Bild-Untertitel → Text (OCR)** | Geplante Aufgabe. Eingebettete PGS-Untertitel (Blu-ray) werden per eigenem PGS-Decoder und PaddleOCR (PP-OCRv5 Latin, ONNX) einmalig in `Film.deu.OCR.srt` umgewandelt. |
| **Fehlende Untertitel herunterladen** | Geplante Aufgabe sowie Eintrag „SubtitleForge“ in Jellyfins Untertitelsuche. Quellen sind SubDL und OpenSubtitles.com (OpenSubtitles automatisch nur bei Hash-Treffer, um das Tageslimit zu schonen). Die Kandidaten werden danach bewertet, wie gut sie zum Release passen (Gruppe, Quelle, Dienst, Auflösung, Fassung, Folge). |
| **Sprachen pro Film/Serie/Bibliothek** | Tag `subs:de,ja` im Metadaten-Editor (an der Serie gilt er für alle Folgen, `subs:none` schaltet ab) → Bibliothekseinstellung „Untertitel herunterladen → Sprachen“ → Plugin-Standard. |
| **Forced-Erkennung** | Forced-Untertitel (nur Schilder/Fremdsprache) werden am Release-Namen und an der geringen Abdeckung der Dialoge erkannt und als `.forced` gespeichert, nie als voller Untertitel. |
| **Spracherkennung (Whisper)** | Erzeugt Untertitel aus einer Tonspur (CPU, whisper.cpp): manuell pro Film/Folge mit frei wählbarer Tonspur und Zielsprache. Automatisch als letzter Ausweg nur für Serien/Filme mit dem Tag `whisper:auto` (oder global eingeschaltet; `whisper:off` schließt aus). Optional ersetzt das Ergebnis einen heruntergeladenen Untertitel, der dann nie wieder geladen wird. |
| **Offline-Übersetzung** | Weicht die Tonspur-Sprache von der Zielsprache ab, wird transkribiert und lokal übersetzt: Deutsch↔Englisch (Opus-MT), Japanisch↔Englisch (FuguMT), Japanisch↔Deutsch über Englisch. Ohne passende Tonspur nimmt das Plugin automatisch die am besten erkannte (z.B. deutschen Dub vor Japanisch). Qualität: gut für Deutsch↔Englisch, für Japanisch eher zum Verstehen. |
| **Neue Einträge sofort** | Neu hinzugefügte Filme und Folgen werden automatisch bearbeitet, sobald der Bibliotheks-Scan ruhig ist. |
| **Übersicht** | Die Plugin-Seite zeigt pro Film/Folge, welche Sprachen vorhanden sind oder fehlen und warum (nichts gefunden, verworfen wegen Asynchronität …), mit den Aktionen „Neu suchen“ und „Whisper“. |
| **Synchronität prüfen und korrigieren** | Silero VAD erkennt Sprache in der Tonspur (bei Surround-Ton im Center-Kanal). Der Abgleich korrigiert Versatz, Framerate (23,976/24/25) und Schnittunterschiede, sprachunabhängig. Untertitel, die nicht zur Tonspur passen, werden verworfen; bereits synchrone Untertitel bleiben unverändert. |

## Geplante Aufgaben (Dashboard → Geplante Aufgaben → SubtitleForge)

| Aufgabe | Standard-Zeitplan |
|---|---|
| Bild-Untertitel in Text umwandeln (OCR) | täglich 03:00 |
| Fehlende Untertitel herunterladen (Whisper-Fallback nur, wenn aktiviert) | täglich 04:00 |
| Untertitel-Synchronität prüfen und korrigieren | nur manuell |

## Bauen

```bash
dotnet publish Jellyfin.Plugin.SubtitleForge/Jellyfin.Plugin.SubtitleForge.csproj -c Release -o out
```

Den Inhalt von `out/` (DLLs und `runtimes/linux-*`) nach `<jellyfin-config>/plugins/SubtitleForge/` kopieren und Jellyfin neu starten. Ohne lokales .NET SDK baut `dev-deploy.sh` das Plugin in einem Podman-Container und spielt es in eine lokale Test-Instanz ein.

## Tests

```bash
dotnet test Jellyfin.Plugin.SubtitleForge.Tests
```

- Die Sync-Tests nutzen einen synthetischen Film mit Sprachausgabe (`tools/make_synth_film.py` erzeugt `Fixtures/synth/film.wav`).
- `SF_REAL_DIR` und `SF_ANIME_DIR` aktivieren die Kalibrierungstests mit echten Tonspuren und Referenz-Untertiteln (siehe `RealMediaSyncTests.cs` und `AnimeSyncTests.cs`).
- `SF_DUMP_PGS=<datei.sup>` exportiert die PGS-Bilder und das OCR-Ergebnis zur Fehlersuche.

## Lizenz

GPLv3 (siehe `LICENSE`). Fremdkomponenten: siehe `THIRD-PARTY-NOTICES.md`.
