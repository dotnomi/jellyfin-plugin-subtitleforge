# Plan v0.2 – fertiges Produkt vor dem Server-Test

**Status: umgesetzt.** Whisper.net 1.8.1 statt 1.9.1, weil 1.9 `Microsoft.Extensions.AI.Abstractions` braucht und diese mit Jellyfin 10.11 kollidiert. `libgomp` wird mitgeliefert, weil sie im Jellyfin-Image fehlt. Zusätzlich gibt es eine manuelle Whisper-Aktion mit „Download ersetzen“ und eine Sperrliste.

Ziel: Alle Kernfunktionen sind fertig, bevor SubtitleForge auf dem echten Server läuft.

## 1. Sprachen pro Film/Serie/Bibliothek
- Die **spezifischste Einstellung gewinnt**: Tag am Item oder an der Serie → Bibliothekseinstellung → Plugin-Standard.
  - Tag `subs:de,ja` im Jellyfin-Metadaten-Editor; an der Serie gesetzt, gilt er für alle Folgen. `subs:none` schaltet die Serie ab.
  - Bibliothek: Jellyfins eigene Einstellung „Untertitel herunterladen → Sprachen“ (`LibraryOptions.SubtitleDownloadLanguages`).
  - Plugin-Standard: `DownloadLanguages`.
- Das gilt für OCR, Download und Whisper gleichermaßen (`LanguageResolver`).

## 2. Forced-Erkennung
- Downloads gelten als Forced, wenn
  - der Release-Name `forced`/`foreign` enthält, oder
  - der Untertitel weniger als 25 % der erkannten Sprechphasen abdeckt.

  Forced-Downloads werden als `.forced` gespeichert, nie als voller Untertitel.
- OCR: Tragen alle PGS-Einträge einer Spur das Forced-Flag, wird sie auch dann als Forced behandelt, wenn der Container sie nicht so kennzeichnet.

## 3. Neue Items sofort bearbeiten
- `ItemAdded`-Ereignis → Warteschlange, nach 10 min Ruhe (Scan beendet) Abarbeitung pro Item: OCR → Download → Whisper-Fallback.
- Ein gemeinsamer `ItemProcessor` für die Warteschlange, die geplanten Aufgaben und manuelle Aktionen. Höchstens ein schwerer Job gleichzeitig.

## 4. Whisper (nur CPU)
- Whisper.net 1.9.1 mit CPU-Laufzeit (linux-x64/arm64). Windows-Natives werden entfernt, die Natives werden aus dem Plugin-Ordner geladen.
- Das Modell wird beim ersten Einsatz von Hugging Face geladen (ggml, quantisiert) und im Plugin-Datenordner abgelegt. Wählbar sind `base`, `small` (Standard), `medium` und `large-v3-turbo`.
  - `large-v3-turbo` kann nur transkribieren, nicht übersetzen.
- Transkribiert werden nur die Sprachabschnitte aus der VAD; das spart Zeit und verhindert erfundenen Text in Stille. Dazu kommt ein Filter gegen typische Halluzinationen („Untertitel im Auftrag des ZDF“, Wiederholungen).
- **Sprache → Tonspur**:
  - Gewünschte Sprache = Sprache einer Tonspur → transkribieren. Deutsch aus dem deutschen Dub.
  - Englisch ohne englische Tonspur → aus der Originalspur nach Englisch übersetzen (nur `small`/`medium`).
  - Sonst: nicht möglich, wird protokolliert.
- Die Ausgabe wird in lesbare Untertitel umgebrochen (≤ 42 Zeichen pro Zeile, 2 Zeilen, ≤ 6 s pro Untertitel) und als `Film.ger.Whisper.srt` gespeichert. Der Spurtitel „Whisper“ kennzeichnet ihn als maschinell erzeugt.
- Automatisch nur, wenn kein passender Untertitel gefunden wurde (abschaltbar). Manuell über die Plugin-Seite.

## 5. Übersicht und manuelle Aktionen (Plugin-Seite)
- Eine Liste pro Item: vorhandene Sprachen, fehlende Sprachen, letzter Versuch und Ergebnis (gefunden / verworfen wegen Asynchronität / nichts gefunden / Whisper).
- Aktionen: „Jetzt erneut suchen“, „Mit Whisper erzeugen“ (Tonspur und Zielsprache wählbar), „Sync prüfen“.
- API: `/SubtitleForge/Report`, `/SubtitleForge/Items/{id}/Process`, nur für Admins.

## Reihenfolge
1. Whisper-Spike: Natives laden im Jellyfin-Prozess, Geschwindigkeit messen (höchstes Risiko zuerst).
2. LanguageResolver, Forced-Erkennung, ItemProcessor.
3. Whisper-Integration.
4. Warteschlange für neue Items.
5. Übersicht und Aktionen.
6. Tests (Unit-Tests, Kalibrierung, Live im Test-Jellyfin), dann Commit.
