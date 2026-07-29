# Neolink.NET

[English](README.md) · [Français](README.fr.md) · **Deutsch** · [Español](README.es.md) · [Nederlands](README.nl.md) · [Polski](README.pl.md) · [Português](README.pt.md)

> **Hinweis**: Diese Übersetzung wurde von einer KI erstellt und konnte nicht
> vollständig auf Richtigkeit geprüft werden. Maßgeblich ist die
> [englische Dokumentation](README.md); Korrekturen sind willkommen.

**RTSP-Brücke + Web-Viewer für Reolink-Kameras, die das proprietäre Baichuan-Protokoll sprechen.**

Neolink.NET ist für Reolink-IP-Kameras gedacht, die das proprietäre
„Baichuan“-Protokoll auf TCP-Port 9000 statt Standard-RTSP/ONVIF sprechen
(B800/D800, B400/D400, E1, Lumus, 510A, Duo, TrackMix und viele andere).

Ihre NVR-Software (**Frigate**, Blue Iris, Home Assistant, Shinobi, VLC,
ffmpeg …) verbindet sich mit Neolink.NET, das sich bei der Kamera anmeldet,
ihren Medienstrom demultiplext und ihn als standardkonformes RTSP neu
ausliefert. Obendrauf bringt Neolink.NET eine **eingebaute Browser-Oberfläche**
mit — eine Multi-Kamera-Wand mit Live-Video bei geringer Latenz, ohne Plugins
und ohne Transkodierung — sowie eine native **MQTT-Integration für Home
Assistant**: Jede Kamera erscheint automatisch in HA (über MQTT Discovery) mit
Erkennungssensoren, Bedienelementen und Verfügbarkeit.

Die Kameras bleiben unverändert, und es wird kein Reolink-NVR benötigt.

## Highlights

- **RTSP-Brücke**: H.264/H.265 und AAC werden neu verpackt, nie neu kodiert;
  eine Kameraverbindung versorgt beliebig viele Clients
- **Web-Oberfläche**: Kamerawand (fünf Layouts), Ereignisse, synchronisierte
  Multi-Kamera-Zeitleiste mit Export, von der Kamera erkannte Einstellungen
  (PTZ, Zoom, Lichter, Sirene …), **mehrere Sprachen** (darunter Deutsch),
  live angewendet
- **Akkukameras** (Argus usw.) werden automatisch erkannt und schlaffreundlich behandelt
- **Aufzeichnung**: Ereignis-Clips + 24/7, gestufter Speicher, Verschlüsselung
  ruhender Daten, E-Mail- und Browser-Alarme
- **Home Assistant / MQTT**: ein Gerät pro Kamera, ohne YAML
- **KI-Ereignisbeschreibungen** (Beta) über ein bildfähiges LLM, lokal oder gehostet

## Schnellstart (Docker)

```bash
docker pull ghcr.io/borexola/neolink.net:latest
mkdir -p config
curl -o config/config.json https://raw.githubusercontent.com/borexola/neolink.net/main/src/Neolink.Server/config.example.json
# config.json bearbeiten: Kameranamen, IP-Adressen, Zugangsdaten (dieselben wie in der Reolink-App)

docker run -d --name neolink --restart unless-stopped \
    -p 8654:8654 -p 8655:8655 \
    -e TZ=Europe/Berlin \
    -v "$PWD/config:/config" \
    ghcr.io/borexola/neolink.net:latest
```

Web-Oberfläche unter `http://<host>:8655`, RTSP-Streams unter
`rtsp://<host>:8654/<Kameraname>`. Ein **Home-Assistant-Add-on** ist ebenfalls
verfügbar — siehe die englische Dokumentation.

Die Sprache der Oberfläche wird beim ersten Start gewählt (Dialog „Diesen
Server sichern“) oder unter ⚙ → Sprache und gilt sofort — ohne Neustart und
ohne Neuladen.

## Vollständige Dokumentation

Die ausführliche Dokumentation (Konfiguration, Home Assistant, Akkukameras,
gestufter Speicher, Windows-Desktop-App …) findet sich im
[englischen README](README.md) und im Ordner [docs/](docs/).
