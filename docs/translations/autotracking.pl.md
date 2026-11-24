# Sterowanie kliknięciem i autośledzenie we Frigate

[English](../autotracking.md) · [Français](autotracking.fr.md) · [Deutsch](autotracking.de.md) · [Español](autotracking.es.md) · [Nederlands](autotracking.nl.md) · **Polski** · [Português](autotracking.pt.md)

> **Uwaga**: to tłumaczenie wykonała SI. Rozstrzygająca jest
> [wersja angielska](../autotracking.md).

> **Beta.** Przetestowane z Reolink E1 Pro.

Sterowanie kliknięciem i autośledzenie we Frigate wymagają kamery, która przesuwa się o część obrazu
(ruchy względne ONVIF, `TranslationSpaceFov`). Głowice Reolink tego nie potrafią, więc punkt PTZ
Neolink.NET zgłasza tę możliwość i wykonuje pracę za nie:

- **Obrót i pochylenie** są odmierzane na podstawie kąta widzenia obiektywu i prędkości głowicy.
- **Opóźnienie startu** jest uczone samo na kamerach, które podają położenie głowicy.
- **Obiektywy z zoomem** przyjmują też dokładne pozycje zoomu.

## 1. Neolink.NET

**Ustawienia serwera → Kamery → Edytuj** kamerę:

1. W *Połączenie zewnętrzne* ustaw **PTZ dla Frigate** na **Wspólny port**.
2. W *Sterowanie kliknięciem i autośledzenie* zaznacz **Ta kamera obraca się i pochyla**.
3. Wpisz poziomy kąt widzenia obiektywu z karty katalogowej.
4. Zmierz czas pełnego obrotu i pełnego pochylenia (od ogranicznika do ogranicznika, prędkość 32 w panelu kamery).
5. Zapisz i uruchom ponownie Neolink.NET.

![Sekcja sterowania kliknięciem w edytorze kamery z wartościami Reolink E1 Pro](../autotracking-editor.png)

*Reolink E1 Pro: 87,5°, pełny obrót w 6 s na 355°, pełne pochylenie w 2 s na 50°.*

**Edytujesz `config.json`?** Dodaj to do wpisu kamery. Prędkości to stopnie ÷ sekundy.

```jsonc
{
  "name": "e1pro",
  "address": "192.168.1.60",
  "username": "admin",
  "password": "…",
  "ptz_share": true,       // PTZ dla Frigate na wspólnym porcie (8656)
  "ptz_fov_h": 87.5,       // wymagane: poziomy kąt obiektywu, z karty katalogowej
  "ptz_pan_rate": 59.2,    // wymagane: °/s obrotu, 355° w 6 s
  "ptz_tilt_rate": 25      // °/s pochylenia, 50° w 2 s (domyślnie: jak obrót)
  // Opcjonalne: "ptz_fov_v" (kąt pionowy), "ptz_speed" (mierzona prędkość, domyślnie 32),
  // "ptz_lag_ms" (rozruch silnika, uczony), "ptz_tele_fov_h" (zoom: kąt przy pełnym zbliżeniu)
}
```

## 2. Frigate

W aplikacji Reolink zapisz preset o nazwie `home`, do którego kamera będzie wracać.
Następnie dodaj to do konfiguracji Frigate. Zastąp `e1pro` nazwą kamery w Neolink.NET i podaj użytkownika RTSP z dostępem do niej.

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
        coordinates: 0,0,1,0,1,1,0,1   # cały obraz
    onvif:
      host: <neolink-host>
      port: 8656
      user: <user>
      password: <password>
      profile: e1pro
      autotracking:
        enabled: true
        calibrate_on_startup: true   # po pierwszym uruchomieniu z powrotem na false
        zooming: disabled
        track: [person]
        required_zones: [tracking_area]
        return_preset: home
        timeout: 10
```

## 3. Test

1. **Sterowanie kliknięciem:** w podglądzie na żywo we Frigate kliknij w punkt. Głowica powinna go wyśrodkować.
   - Jeśli go minie, skróć czas tej osi w Neolink.NET.
   - Jeśli zatrzyma się przed nim, wydłuż go.
   - W `config.json` odwrotnie: zwiększ prędkość, jeśli minie punkt, zmniejsz, jeśli zatrzyma się przed nim.
2. **Kalibracja:** uruchom ponownie Frigate i nie ruszaj kamery przez około dwie minuty.
   Frigate sam zapisze `movement_weights`; potem ustaw `calibrate_on_startup: false`.
3. **Śledzenie:** wejdź w kadr. Kamera podąża za tobą i wraca do `home` po `timeout` sekundach.

Autośledzenie wymaga szybkiego detektora (Coral, OpenVINO lub GPU). Pomaga maska ruchu na znaczniku czasu kamery.
Frigate 0.17 lub starszy? Ustaw **PTZ dla Frigate** na **Własny port** i użyj go zamiast 8656.
