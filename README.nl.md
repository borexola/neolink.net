# Neolink.NET

[English](README.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [Español](README.es.md) · **Nederlands** · [Polski](README.pl.md) · [Português](README.pt.md)

> **Let op**: deze vertaling is door AI gegenereerd en kon niet volledig op
> juistheid worden gecontroleerd. De [Engelse documentatie](README.md) is
> leidend; correcties zijn welkom.

**RTSP-brug + webviewer voor Reolink-camera's die het propriëtaire Baichuan-protocol spreken.**

Neolink.NET is voor Reolink-IP-camera's die het propriëtaire
„Baichuan"-protocol op TCP-poort 9000 spreken in plaats van standaard
RTSP/ONVIF (B800/D800, B400/D400, E1, Lumus, 510A, Duo, TrackMix en vele
andere).

Uw NVR-software (**Frigate**, Blue Iris, Home Assistant, Shinobi, VLC,
ffmpeg…) verbindt met Neolink.NET, dat inlogt op de camera, haar mediastream
demultiplext en opnieuw aanbiedt als standaardconform RTSP. Daarbovenop levert
Neolink.NET een **ingebouwde browserinterface** — een multi-camerawand met
live video met lage vertraging, zonder plugins of transcodering — en een
native **MQTT-integratie voor Home Assistant**: elke camera verschijnt
automatisch in HA (via MQTT Discovery) met detectiesensoren, bediening en
beschikbaarheid.

De camera's blijven ongewijzigd en er is geen Reolink-NVR nodig.

## Hoogtepunten

- **RTSP-brug**: H.264/H.265 en AAC worden herverpakt, nooit opnieuw
  gecodeerd; één cameraverbinding voedt elk aantal clients
- **Webinterface**: camerawand (vijf indelingen), gebeurtenissen,
  gesynchroniseerde multi-cameratijdlijn met export, camera-instellingen
  ontdekt vanaf het apparaat zelf (PTZ, zoom, verlichting, sirene…),
  **meerdere talen** (waaronder Nederlands), live toegepast
- **Accucamera's** (Argus enz.) automatisch herkend en slaapvriendelijk
- **Opname**: gebeurtenisclips + 24/7, gelaagde opslag, versleuteling in rust,
  e-mail- en browsermeldingen
- **Home Assistant / MQTT**: één apparaat per camera, zonder YAML
- **AI-gebeurtenisbeschrijvingen** (bèta) via een beeldbekwaam LLM, lokaal of gehost

## Snelstart (Docker)

```bash
docker pull ghcr.io/borexola/neolink.net:latest
mkdir -p config
curl -o config/config.json https://raw.githubusercontent.com/borexola/neolink.net/main/src/Neolink.Server/config.example.json
# Bewerk config.json: cameranamen, IP-adressen en inloggegevens (dezelfde als de Reolink-app)

docker run -d --name neolink --restart unless-stopped \
    -p 8654:8654 -p 8655:8655 \
    -e TZ=Europe/Amsterdam \
    -v "$PWD/config:/config" \
    ghcr.io/borexola/neolink.net:latest
```

Webinterface op `http://<host>:8655`, RTSP-streams op
`rtsp://<host>:8654/<Cameranaam>`. Er is ook een **Home Assistant-add-on** —
zie de Engelse documentatie.

De taal van de interface kiest u bij de eerste start (dialoog „Deze server
beveiligen") of onder ⚙ → Taal, en ze geldt direct — zonder herstart en zonder
herladen.

## Volledige documentatie

De uitgebreide documentatie (configuratie, Home Assistant, accucamera's,
gelaagde opslag, Windows-desktopapp…) staat in de
[Engelse README](README.md) en de map [docs/](docs/).
