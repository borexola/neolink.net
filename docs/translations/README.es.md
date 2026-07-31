# Neolink.NET

[English](../../README.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · **Español** · [Nederlands](README.nl.md) · [Polski](README.pl.md) · [Português](README.pt.md)

> **Nota**: esta traducción fue generada por IA y no ha podido verificarse por
> completo. La [documentación en inglés](../../README.md) es la referencia; las
> correcciones son bienvenidas.

**Puente RTSP + visor web para cámaras Reolink que hablan el protocolo propietario Baichuan.**

Neolink.NET es para cámaras IP Reolink que usan el protocolo propietario
«Baichuan» en el puerto TCP 9000 en lugar de RTSP/ONVIF estándar (B800/D800,
B400/D400, E1, Lumus, 510A, Duo, TrackMix y muchas otras).

Su software NVR (**Frigate**, Blue Iris, Home Assistant, Shinobi, VLC,
ffmpeg…) se conecta a Neolink.NET, que inicia sesión en la cámara,
demultiplexa su flujo multimedia y lo vuelve a servir como RTSP conforme a los
estándares. Además, Neolink.NET incluye una **interfaz web integrada** — un
muro multicámara con vídeo en directo de baja latencia, sin plugins ni
transcodificación — y una **integración MQTT nativa para Home Assistant**:
cada cámara aparece automáticamente en HA (mediante MQTT Discovery) con
sensores de detección, controles y disponibilidad.

Las cámaras no se modifican y no se necesita ningún NVR de Reolink.

## Puntos destacados

- **Puente RTSP**: H.264/H.265 y AAC reempaquetados, nunca recodificados; una
  sola conexión de cámara alimenta cualquier número de clientes
- **Interfaz web**: muro de cámaras (cinco disposiciones), eventos, cronología
  multicámara sincronizada con exportación, ajustes de cámara descubiertos del
  propio dispositivo (PTZ, zoom, luces, sirena…), **varios idiomas** (incluido
  el español) aplicados en vivo
- **Cámaras de batería** (Argus, etc.) autodetectadas y respetuosas con el sueño
- **Grabación**: clips de eventos + 24/7, almacenamiento por niveles, cifrado
  en reposo, alertas por correo y navegador
- **Home Assistant / MQTT**: un dispositivo por cámara, sin YAML
- **Descripciones de eventos por IA** (beta) mediante un LLM con visión, local u hospedado

## Inicio rápido (Docker)

```bash
docker pull ghcr.io/borexola/neolink.net:latest
mkdir -p config
curl -o config/config.json https://raw.githubusercontent.com/borexola/neolink.net/main/src/Neolink.Server/config.example.json
# Edite config.json: nombres de cámaras, direcciones IP y credenciales (las mismas que la aplicación Reolink)

docker run -d --name neolink --restart unless-stopped \
    -p 8654:8654 -p 8655:8655 \
    -e TZ=Europe/Madrid \
    -v "$PWD/config:/config" \
    ghcr.io/borexola/neolink.net:latest
```

Interfaz web en `http://<host>:8655`, flujos RTSP en
`rtsp://<host>:8654/<NombreCámara>`. También hay un **complemento de Home
Assistant** — consulte la documentación en inglés.

El idioma de la interfaz se elige en el primer arranque (diálogo «Proteger
este servidor») o en ⚙ → Idioma, y se aplica al instante — sin reinicio ni
recarga.

## Documentación completa

La documentación detallada (configuración, Home Assistant, cámaras de batería,
almacenamiento por niveles, aplicación de escritorio de Windows…) está en el
[README en inglés](../../README.md) y en la carpeta [docs/](../).
