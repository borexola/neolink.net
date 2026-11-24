# Klicksteuerung und Autotracking in Frigate

[English](../autotracking.md) · [Français](autotracking.fr.md) · **Deutsch** · [Español](autotracking.es.md) · [Nederlands](autotracking.nl.md) · [Polski](autotracking.pl.md) · [Português](autotracking.pt.md)

> **Hinweis**: Diese Übersetzung wurde von einer KI erstellt. Maßgeblich ist die
> [englische Fassung](../autotracking.md).

> **Beta.** Getestet mit einer Reolink E1 Pro.

Frigates Klicksteuerung und Autotracking brauchen eine Kamera, die sich um einen Teil des Bildes bewegt
(relative ONVIF-Bewegungen, `TranslationSpaceFov`). Reolink-Köpfe können das nicht, also meldet der
PTZ-Endpunkt von Neolink.NET diese Fähigkeit und übernimmt die Arbeit:

- **Schwenk und Neigung** werden aus dem Bildwinkel des Objektivs und der Drehgeschwindigkeit des Kopfes getimt.
- **Die Anlaufverzögerung** wird von selbst gelernt, wenn die Kamera die Position ihres Kopfes meldet.
- **Zoomobjektive** nehmen außerdem exakte Zoompositionen an.

## 1. Neolink.NET

**Servereinstellungen → Kameras → Bearbeiten** für die Kamera:

1. Unter *Externe Verbindung* **PTZ für Frigate** auf **Gemeinsamer Port** stellen.
2. Unter *Klicksteuerung und Autotracking* **Diese Kamera schwenkt und neigt** anhaken.
3. Den horizontalen Bildwinkel aus dem Datenblatt eintragen.
4. Einen vollen Schwenk und eine volle Neigung stoppen (Anschlag zu Anschlag, Geschwindigkeit 32 im Kamera-Panel).
5. Speichern und Neolink.NET neu starten.

![Der Klicksteuerungs-Abschnitt im Kamera-Editor mit den Werten einer Reolink E1 Pro](../autotracking-editor.png)

*Reolink E1 Pro: 87,5°, ein voller Schwenk in 6 s für 355°, eine volle Neigung in 2 s für 50°.*

**Bearbeiten Sie stattdessen `config.json`?** Fügen Sie dies dem Eintrag der Kamera hinzu. Raten sind Grad ÷ Sekunden.

```jsonc
{
  "name": "e1pro",
  "address": "192.168.1.60",
  "username": "admin",
  "password": "…",
  "ptz_share": true,       // PTZ für Frigate auf dem gemeinsamen Port (8656)
  "ptz_fov_h": 87.5,       // Pflicht: horizontaler Bildwinkel, aus dem Datenblatt
  "ptz_pan_rate": 59.2,    // Pflicht: °/s beim Schwenken, 355° in 6 s
  "ptz_tilt_rate": 25      // °/s beim Neigen, 50° in 2 s (Standard: die Schwenkrate)
  // Optional: "ptz_fov_v" (vertikaler Winkel), "ptz_speed" (gemessene Geschwindigkeit, Standard 32),
  // "ptz_lag_ms" (Motoranlauf, wird gelernt), "ptz_tele_fov_h" (Zoomobjektive: Winkel bei vollem Zoom)
}
```

## 2. Frigate

Speichern Sie in der Reolink-App ein Preset namens `home`, zu dem die Kamera zurückkehrt.
Fügen Sie dann dies zu Frigates Konfiguration hinzu. Ersetzen Sie `e1pro` durch den Kameranamen in Neolink.NET und geben Sie einen RTSP-Benutzer an, der darauf zugreifen darf.

```yaml
go2rtc:
  streams:
    e1pro:
      - rtsp://<user>:<password>@<neolink-host>:8654/e1pro/mainStream
    e1pro_sub:
      - rtsp://<user>:<password>@<neolink-host>:8654/e1pro/subStream

cameras:
  e1pro:
    ffmpeg:
      inputs:
        - path: rtsp://127.0.0.1:8554/e1pro
          input_args: preset-rtsp-restream
          roles: [record]
        - path: rtsp://127.0.0.1:8554/e1pro_sub
          input_args: preset-rtsp-restream
          roles: [detect]
    detect:
      enabled: true
      fps: 5
    zones:
      tracking_area:
        coordinates: 0,0,1,0,1,1,0,1   # das ganze Bild
    onvif:
      host: <neolink-host>
      port: 8656
      user: <user>
      password: <password>
      profile: e1pro
      autotracking:
        enabled: true
        calibrate_on_startup: true   # nach dem ersten Lauf wieder auf false
        zooming: disabled
        track: [person]
        required_zones: [tracking_area]
        return_preset: home
        timeout: 10
```

## 3. Testen

1. **Klicksteuerung:** Klicken Sie in Frigates Live-Ansicht auf eine Stelle. Der Kopf sollte sie zentrieren.
   - Fährt er darüber hinaus, verkürzen Sie die Zeit dieser Achse in Neolink.NET.
   - Bleibt er davor stehen, verlängern Sie sie.
   - In `config.json` umgekehrt: Rate erhöhen, wenn er darüber hinaus fährt, senken, wenn er davor stehen bleibt.
2. **Kalibrieren:** Frigate neu starten und die Kamera etwa zwei Minuten in Ruhe lassen.
   Frigate speichert `movement_weights` selbst; danach `calibrate_on_startup: false` setzen.
3. **Verfolgen:** Gehen Sie ins Bild. Die Kamera folgt Ihnen und kehrt nach `timeout` Sekunden zu `home` zurück.

Autotracking braucht einen schnellen Detektor (Coral, OpenVINO oder eine GPU). Eine Bewegungsmaske über dem Zeitstempel der Kamera hilft.
Frigate 0.17 oder älter? Stellen Sie **PTZ für Frigate** auf **Eigener Port** und verwenden Sie diesen Port statt 8656.
