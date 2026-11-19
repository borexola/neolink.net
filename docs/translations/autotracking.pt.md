# Clique para mover e rastreamento automático no Frigate

[English](../autotracking.md) · [Français](autotracking.fr.md) · [Deutsch](autotracking.de.md) · [Español](autotracking.es.md) · [Nederlands](autotracking.nl.md) · [Polski](autotracking.pl.md) · **Português**

> **Nota**: esta tradução foi feita por IA. A
> [versão em inglês](../autotracking.md) é a de referência.

> **Experimental.** Testado com uma Reolink E1 Pro.

O clique para mover e o rastreamento automático do Frigate precisam de uma câmera que se mova por uma parte da imagem
(movimentos relativos ONVIF, `TranslationSpaceFov`). As cabeças Reolink não fazem isso, então o endpoint
PTZ do Neolink.NET anuncia esse recurso e faz o trabalho:

- **Rotação e inclinação** são cronometradas a partir do ângulo de visão da lente e da velocidade da cabeça.
- **O atraso de partida** é aprendido sozinho nas câmeras que informam a posição da cabeça.
- **Lentes com zoom** também aceitam posições de zoom exatas.

## 1. Neolink.NET

**Configurações do servidor → Câmeras → Editar** a câmera:

1. Em *Conexão externa*, defina **PTZ para o Frigate** como **Porta compartilhada**.
2. Em *Clique para mover e rastreamento automático*, marque **Esta câmera gira e inclina**.
3. Informe o ângulo horizontal da lente indicado na ficha técnica.
4. Cronometre uma rotação e uma inclinação completas (de batente a batente, velocidade 32 no painel da câmera).
5. Salve e reinicie o Neolink.NET.

![A seção clique para mover do editor de câmeras com os valores de uma Reolink E1 Pro](../autotracking-editor.png)

*Reolink E1 Pro: 87,5°, uma rotação completa em 6 s para 355°, uma inclinação completa em 2 s para 50°.*

## 2. Frigate

No app Reolink, salve uma predefinição chamada `home` para onde a câmera volta.
Depois adicione isto à configuração do Frigate. Substitua `e1pro` pelo nome da câmera no Neolink.NET e informe um usuário RTSP com acesso a ela.

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
        coordinates: 0,0,1,0,1,1,0,1   # a imagem toda
    onvif:
      host: <neolink-host>
      port: 8656
      user: <user>
      password: <password>
      profile: e1pro
      autotracking:
        enabled: true
        calibrate_on_startup: true   # voltar para false depois da primeira inicialização
        zooming: disabled
        track: [person]
        required_zones: [tracking_area]
        return_preset: home
        timeout: 10
```

## 3. Testar

1. **Clique para mover:** na visualização ao vivo do Frigate, clique em um ponto. A cabeça deve centrá-lo.
   - Se passar do ponto, encurte o tempo desse eixo no Neolink.NET.
   - Se parar antes, aumente-o.
2. **Calibrar:** reinicie o Frigate e não mexa na câmera por cerca de dois minutos.
   O Frigate salva `movement_weights` sozinho; depois defina `calibrate_on_startup: false`.
3. **Rastrear:** entre na imagem. A câmera segue você e volta para `home` após `timeout` segundos.

O rastreamento automático precisa de um detector rápido (Coral, OpenVINO ou uma GPU). Uma máscara de movimento sobre o carimbo de hora da câmera ajuda.
Frigate 0.17 ou anterior? Defina **PTZ para o Frigate** como **Porta própria** e use essa porta no lugar da 8656.
