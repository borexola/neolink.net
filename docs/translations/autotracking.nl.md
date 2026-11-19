# Klikken om te bewegen en autotracking in Frigate

[English](../autotracking.md) · [Français](autotracking.fr.md) · [Deutsch](autotracking.de.md) · [Español](autotracking.es.md) · **Nederlands** · [Polski](autotracking.pl.md) · [Português](autotracking.pt.md)

> **Let op**: deze vertaling is door AI gemaakt. De
> [Engelse versie](../autotracking.md) is leidend.

> **Experimenteel.** Getest met een Reolink E1 Pro.

Frigates klikken om te bewegen en autotracking verplaatsen een camera over een deel van het beeld.
Reolink-koppen kunnen dat niet, dus Neolink.NET doet het voor ze door elke draai en kanteling te timen.

## 1. Neolink.NET

**Serverinstellingen → Camera's → Bewerken** voor de camera:

1. Zet onder *Externe verbinding* **PTZ voor Frigate** op **Gedeelde poort**.
2. Vink onder *Klikken om te bewegen en autotracking* **Deze camera kan pannen en kantelen** aan.
3. Vul de horizontale beeldhoek van de lens in uit de specificaties.
4. Time een volledige draai en kanteling (van aanslag tot aanslag, snelheid 32 in het camerapaneel).
5. Sla op en herstart Neolink.NET.

![Het klikken-om-te-bewegen-gedeelte van de camera-editor met de waarden van een Reolink E1 Pro](../autotracking-editor.png)

*Reolink E1 Pro: 87,5°, een volledige draai in 6 s voor 355°, een volledige kanteling in 2 s voor 50°.*

## 2. Frigate

Sla in de Reolink-app een preset op met de naam `home`, waar de camera naar terugkeert.
Voeg daarna dit toe aan de Frigate-configuratie. Vervang `e1pro` door de cameranaam in Neolink.NET en geef een RTSP-gebruiker op die toegang heeft.

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
        coordinates: 0,0,1,0,1,1,0,1   # het hele beeld
    onvif:
      host: <neolink-host>
      port: 8656
      user: <user>
      password: <password>
      profile: e1pro
      autotracking:
        enabled: true
        calibrate_on_startup: true   # na de eerste start terug op false
        zooming: disabled
        track: [person]
        required_zones: [tracking_area]
        return_preset: home
        timeout: 10
```

## 3. Testen

1. **Klikken om te bewegen:** klik in Frigates live-weergave op een plek. De kop moet die centreren.
   - Schiet hij voorbij, maak de tijd van die as in Neolink.NET korter.
   - Stopt hij te vroeg, maak hem langer.
2. **Kalibreren:** herstart Frigate en laat de camera ongeveer twee minuten met rust.
   Frigate slaat `movement_weights` zelf op; zet daarna `calibrate_on_startup: false`.
3. **Volgen:** loop in beeld. De camera volgt je en keert na `timeout` seconden terug naar `home`.

Autotracking heeft een snelle detector nodig (Coral, OpenVINO of een GPU). Een bewegingsmasker over de tijdstempel van de camera helpt.
Frigate 0.17 of ouder? Zet **PTZ voor Frigate** op **Eigen poort** en gebruik die poort in plaats van 8656.
