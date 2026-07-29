# Neolink.NET

[English](README.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [Español](README.es.md) · [Nederlands](README.nl.md) · **Polski** · [Português](README.pt.md)

> **Uwaga**: to tłumaczenie zostało wygenerowane przez AI i nie mogło zostać w
> pełni zweryfikowane. Obowiązująca jest [dokumentacja angielska](README.md);
> poprawki są mile widziane.

**Most RTSP + przeglądarka web dla kamer Reolink mówiących własnościowym protokołem Baichuan.**

Neolink.NET jest dla kamer IP Reolink, które używają własnościowego protokołu
„Baichuan" na porcie TCP 9000 zamiast standardowego RTSP/ONVIF (B800/D800,
B400/D400, E1, Lumus, 510A, Duo, TrackMix i wiele innych).

Twoje oprogramowanie NVR (**Frigate**, Blue Iris, Home Assistant, Shinobi,
VLC, ffmpeg…) łączy się z Neolink.NET, który loguje się do kamery,
demultipleksuje jej strumień i serwuje go ponownie jako zgodny ze standardami
RTSP. Ponadto Neolink.NET zawiera **wbudowany interfejs przeglądarkowy** —
ścianę wielu kamer z obrazem na żywo o niskim opóźnieniu, bez wtyczek i bez
transkodowania — oraz natywną **integrację MQTT z Home Assistant**: każda
kamera pojawia się w HA automatycznie (przez MQTT Discovery) z czujnikami
detekcji, sterowaniem i dostępnością.

Kamery pozostają niezmodyfikowane i nie jest wymagany żaden NVR Reolinka.

## Najważniejsze cechy

- **Most RTSP**: H.264/H.265 i AAC przepakowywane, nigdy nie rekodowane; jedno
  połączenie z kamerą obsługuje dowolną liczbę klientów
- **Interfejs web**: ściana kamer (pięć układów), zdarzenia, zsynchronizowana
  wielokamerowa oś czasu z eksportem, ustawienia kamery odkrywane z samego
  urządzenia (PTZ, zoom, światła, syrena…), **wiele języków** (w tym polski),
  stosowanych na żywo
- **Kamery bateryjne** (Argus itd.) wykrywane automatycznie i przyjazne dla snu
- **Nagrywanie**: klipy zdarzeń + 24/7, magazyn warstwowy, szyfrowanie w
  spoczynku, alerty e-mail i przeglądarkowe
- **Home Assistant / MQTT**: jedno urządzenie na kamerę, bez YAML
- **Opisy zdarzeń przez AI** (beta) przez LLM z obsługą obrazu, lokalny lub hostowany

## Szybki start (Docker)

```bash
docker pull ghcr.io/borexola/neolink.net:latest
mkdir -p config
curl -o config/config.json https://raw.githubusercontent.com/borexola/neolink.net/main/src/Neolink.Server/config.example.json
# Edytuj config.json: nazwy kamer, adresy IP i dane logowania (te same co w aplikacji Reolink)

docker run -d --name neolink --restart unless-stopped \
    -p 8654:8654 -p 8655:8655 \
    -e TZ=Europe/Warsaw \
    -v "$PWD/config:/config" \
    ghcr.io/borexola/neolink.net:latest
```

Interfejs web pod `http://<host>:8655`, strumienie RTSP pod
`rtsp://<host>:8654/<NazwaKamery>`. Dostępny jest też **dodatek Home
Assistant** — zobacz dokumentację angielską.

Język interfejsu wybiera się przy pierwszym uruchomieniu (okno „Zabezpiecz ten
serwer") lub w ⚙ → Język i obowiązuje natychmiast — bez restartu i bez
przeładowania.

## Pełna dokumentacja

Szczegółowa dokumentacja (konfiguracja, Home Assistant, kamery bateryjne,
magazyn warstwowy, aplikacja desktopowa Windows…) znajduje się w
[angielskim README](README.md) i w katalogu [docs/](docs/).
