# Neolink.NET

[English](../../README.md) · **Français** · [Deutsch](README.de.md) · [Español](README.es.md) · [Nederlands](README.nl.md) · [Polski](README.pl.md) · [Português](README.pt.md)

> **Note** : cette traduction a été générée par IA et n'a pas pu être entièrement
> vérifiée. La [documentation anglaise](../../README.md) fait foi ; les corrections
> sont les bienvenues.

**Pont RTSP + visionneuse web pour les caméras Reolink qui parlent le protocole propriétaire Baichuan.**

Neolink.NET s'adresse aux caméras IP Reolink qui utilisent le protocole
propriétaire « Baichuan » sur le port TCP 9000 au lieu de RTSP/ONVIF standard
(B800/D800, B400/D400, E1, Lumus, 510A, Duo, TrackMix et bien d'autres).

Votre logiciel NVR (**Frigate**, Blue Iris, Home Assistant, Shinobi, VLC,
ffmpeg…) se connecte à Neolink.NET, qui se connecte à la caméra, démultiplexe
son flux média et le ressert en RTSP conforme aux standards. Par-dessus,
Neolink.NET embarque une **interface web intégrée** — un mur multi-caméras avec
vidéo en direct à faible latence, sans plugins ni transcodage — et une
**intégration MQTT native pour Home Assistant** : chaque caméra apparaît
automatiquement dans HA (via MQTT Discovery) avec capteurs de détection,
commandes et disponibilité.

Les caméras ne sont pas modifiées et aucun NVR Reolink n'est requis.

## Points forts

- **Pont RTSP** : H.264/H.265 et AAC réempaquetés, jamais réencodés ; une seule
  connexion caméra alimente un nombre illimité de clients
- **Interface web** : mur de caméras (cinq dispositions), événements,
  chronologie multi-caméras synchronisée avec export, réglages caméra découverts
  depuis l'appareil (PTZ, zoom, éclairages, sirène…), **plusieurs langues**
  (dont le français) appliquées en direct
- **Caméras batterie** (Argus, etc.) auto-détectées et respectueuses du sommeil
- **Enregistrement** : clips d'événements + 24/7, stockage à niveaux,
  chiffrement au repos, alertes e-mail et navigateur
- **Home Assistant / MQTT** : un appareil par caméra, sans YAML
- **Descriptions d'événements par IA** (bêta) via un LLM à vision, local ou hébergé

## Démarrage rapide (Docker)

```bash
docker pull ghcr.io/borexola/neolink.net:latest
mkdir -p config
curl -o config/config.json https://raw.githubusercontent.com/borexola/neolink.net/main/src/Neolink.Server/config.example.json
# Éditez config.json : noms de caméras, adresses IP, identifiants (les mêmes que l'application Reolink)

docker run -d --name neolink --restart unless-stopped \
    -p 8654:8654 -p 8655:8655 \
    -e TZ=Europe/Paris \
    -v "$PWD/config:/config" \
    ghcr.io/borexola/neolink.net:latest
```

Interface web sur `http://<hôte>:8655`, flux RTSP sur
`rtsp://<hôte>:8654/<NomCaméra>`. Un module complémentaire **Home Assistant**
est également disponible — voir la documentation anglaise.

La langue de l'interface se choisit au premier démarrage (dialogue « Sécuriser
ce serveur ») ou dans ⚙ → Langue, et s'applique instantanément — sans
redémarrage ni rechargement.

## Documentation complète

La documentation détaillée (configuration, Home Assistant, caméras batterie,
stockage à niveaux, application de bureau Windows…) vit dans le
[README anglais](../../README.md) et le dossier [docs/](../).
