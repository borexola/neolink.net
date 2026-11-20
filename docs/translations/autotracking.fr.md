# Clic pour déplacer et suivi automatique dans Frigate

[English](../autotracking.md) · **Français** · [Deutsch](autotracking.de.md) · [Español](autotracking.es.md) · [Nederlands](autotracking.nl.md) · [Polski](autotracking.pl.md) · [Português](autotracking.pt.md)

> **Remarque** : cette traduction a été produite par une IA. La
> [version anglaise](../autotracking.md) fait foi.

> **Expérimental.** Testé avec une Reolink E1 Pro.

Le clic pour déplacer et le suivi automatique de Frigate demandent une caméra qui se déplace d'une partie de l'image
(déplacements relatifs ONVIF, `TranslationSpaceFov`). Les têtes Reolink ne savent pas le faire ; le point
d'accès PTZ de Neolink.NET annonce cette capacité et fait le travail :

- **Le panoramique et l'inclinaison** sont chronométrés d'après l'angle de vue de l'objectif et la vitesse de la tête.
- **Le délai de démarrage** s'apprend tout seul sur les caméras qui indiquent la position de leur tête.
- **Les objectifs zoom** acceptent aussi des positions de zoom exactes.

## 1. Neolink.NET

**Réglages du serveur → Caméras → Modifier** la caméra :

1. Sous *Connexion externe*, réglez **PTZ pour Frigate** sur **Port partagé**.
2. Sous *Clic pour déplacer et suivi automatique*, cochez **Cette caméra pivote et s'incline**.
3. Saisissez l'angle horizontal de l'objectif indiqué dans sa fiche technique.
4. Chronométrez un panoramique et une inclinaison complets (butée à butée, vitesse 32 dans le panneau de la caméra).
5. Enregistrez, puis redémarrez Neolink.NET.

![La section clic pour déplacer de l'éditeur de caméra avec les valeurs d'une Reolink E1 Pro](../autotracking-editor.png)

*Reolink E1 Pro : 87,5°, un panoramique complet en 6 s pour 355°, une inclinaison complète en 2 s pour 50°.*

**Vous modifiez `config.json` ?** Ajoutez ceci à l'entrée de la caméra. Les vitesses sont des degrés ÷ secondes.

```jsonc
{
  "name": "e1pro",
  "address": "192.168.1.60",
  "username": "admin",
  "password": "…",
  "ptz_share": true,       // PTZ pour Frigate sur le port partagé (8656)
  "ptz_fov_h": 87.5,       // obligatoire : angle horizontal de l'objectif, fiche technique
  "ptz_pan_rate": 59.2,    // obligatoire : °/s en panoramique, 355° en 6 s
  "ptz_tilt_rate": 25      // °/s en inclinaison, 50° en 2 s (par défaut : celle du panoramique)
  // Facultatif : "ptz_fov_v" (angle vertical), "ptz_speed" (vitesse mesurée, 32 par défaut),
  // "ptz_lag_ms" (démarrage du moteur, appris), "ptz_tele_fov_h" (zoom : angle au zoom maximal)
}
```

## 2. Frigate

Dans l'application Reolink, enregistrez un préréglage nommé `home` auquel la caméra reviendra.
Ajoutez ensuite ceci à la configuration de Frigate. Remplacez `e1pro` par le nom de la caméra dans Neolink.NET et indiquez un utilisateur RTSP autorisé sur celle-ci.

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
        coordinates: 0,0,1,0,1,1,0,1   # toute l'image
    onvif:
      host: <neolink-host>
      port: 8656
      user: <user>
      password: <password>
      profile: e1pro
      autotracking:
        enabled: true
        calibrate_on_startup: true   # remettre à false après le premier démarrage
        zooming: disabled
        track: [person]
        required_zones: [tracking_area]
        return_preset: home
        timeout: 10
```

## 3. Tester

1. **Clic pour déplacer :** dans la vue en direct de Frigate, cliquez sur un point. La tête doit le centrer.
   - Si elle le dépasse, raccourcissez la durée de cet axe dans Neolink.NET.
   - Si elle s'arrête avant, allongez-la.
   - Dans `config.json`, c'est l'inverse : augmentez la vitesse si elle dépasse, baissez-la si elle s'arrête avant.
2. **Calibrer :** redémarrez Frigate et ne touchez pas à la caméra pendant environ deux minutes.
   Frigate enregistre `movement_weights` lui-même ; mettez ensuite `calibrate_on_startup: false`.
3. **Suivre :** entrez dans le champ. La caméra vous suit et revient à `home` après `timeout` secondes.

Le suivi automatique demande un détecteur rapide (Coral, OpenVINO ou un GPU). Un masque de mouvement sur l'horodatage de la caméra aide.
Frigate 0.17 ou antérieur ? Réglez **PTZ pour Frigate** sur **Port dédié** et utilisez ce port au lieu de 8656.
