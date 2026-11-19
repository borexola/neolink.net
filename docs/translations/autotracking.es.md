# Clic para mover y seguimiento automático en Frigate

[English](../autotracking.md) · [Français](autotracking.fr.md) · [Deutsch](autotracking.de.md) · **Español** · [Nederlands](autotracking.nl.md) · [Polski](autotracking.pl.md) · [Português](autotracking.pt.md)

> **Nota**: esta traducción la ha generado una IA. La
> [versión en inglés](../autotracking.md) es la de referencia.

> **Experimental.** Probado con una Reolink E1 Pro.

El clic para mover y el seguimiento automático de Frigate mueven una cámara una parte de la imagen.
Los cabezales Reolink no pueden hacerlo, así que Neolink.NET lo hace por ellos cronometrando cada giro e inclinación.

## 1. Neolink.NET

**Ajustes del servidor → Cámaras → Editar** la cámara:

1. En *Conexión externa*, ponga **PTZ para Frigate** en **Puerto compartido**.
2. En *Clic para mover y seguimiento automático*, marque **Esta cámara gira e inclina**.
3. Introduzca el ángulo horizontal del objetivo según su ficha técnica.
4. Cronometre un giro y una inclinación completos (de tope a tope, velocidad 32 en el panel de la cámara).
5. Guarde y reinicie Neolink.NET.

![La sección de clic para mover del editor de cámaras con los valores de una Reolink E1 Pro](../autotracking-editor.png)

*Reolink E1 Pro: 87,5°, un giro completo en 6 s para 355°, una inclinación completa en 2 s para 50°.*

## 2. Frigate

En la app de Reolink, guarde una posición predefinida llamada `home` al que volverá la cámara.
Luego añada esto a la configuración de Frigate. Sustituya `e1pro` por el nombre de la cámara en Neolink.NET e indique un usuario RTSP con acceso a ella.

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
        coordinates: 0,0,1,0,1,1,0,1   # toda la imagen
    onvif:
      host: <neolink-host>
      port: 8656
      user: <user>
      password: <password>
      profile: e1pro
      autotracking:
        enabled: true
        calibrate_on_startup: true   # volver a false tras el primer arranque
        zooming: disabled
        track: [person]
        required_zones: [tracking_area]
        return_preset: home
        timeout: 10
```

## 3. Probar

1. **Clic para mover:** en la vista en directo de Frigate, haga clic en un punto. El cabezal debería centrarlo.
   - Si se pasa del punto, acorte el tiempo de ese eje en Neolink.NET.
   - Si se queda corto, alárguelo.
2. **Calibrar:** reinicie Frigate y no toque la cámara durante unos dos minutos.
   Frigate guarda `movement_weights` por sí mismo; después ponga `calibrate_on_startup: false`.
3. **Seguir:** entre en la imagen. La cámara le sigue y vuelve a `home` tras `timeout` segundos.

El seguimiento automático necesita un detector rápido (Coral, OpenVINO o una GPU). Una máscara de movimiento sobre la marca de tiempo de la cámara ayuda.
¿Frigate 0.17 o anterior? Ponga **PTZ para Frigate** en **Puerto propio** y use ese puerto en lugar de 8656.
