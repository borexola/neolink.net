# Neolink.NET

[English](../../README.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [Español](README.es.md) · [Nederlands](README.nl.md) · [Polski](README.pl.md) · **Português**

> **Nota**: esta tradução foi gerada por IA e não pôde ser totalmente
> verificada. A [documentação em inglês](../../README.md) é a referência; correções
> são bem-vindas.

**Ponte RTSP + visualizador web para câmeras Reolink que falam o protocolo proprietário Baichuan.**

O Neolink.NET é para câmeras IP Reolink que usam o protocolo proprietário
"Baichuan" na porta TCP 9000 em vez de RTSP/ONVIF padrão (B800/D800,
B400/D400, E1, Lumus, 510A, Duo, TrackMix e muitas outras).

Seu software NVR (**Frigate**, Blue Iris, Home Assistant, Shinobi, VLC,
ffmpeg…) conecta-se ao Neolink.NET, que faz login na câmera, demultiplexa o
fluxo de mídia dela e o serve novamente como RTSP conforme os padrões. Além
disso, o Neolink.NET traz uma **interface web integrada** — um mural
multicâmera com vídeo ao vivo de baixa latência, sem plugins nem
transcodificação — e uma **integração MQTT nativa para o Home Assistant**:
cada câmera aparece automaticamente no HA (via MQTT Discovery) com sensores de
detecção, controles e disponibilidade.

As câmeras não são modificadas e nenhum NVR Reolink é necessário.

## Destaques

- **Ponte RTSP**: H.264/H.265 e AAC reempacotados, nunca recodificados; uma
  única conexão com a câmera alimenta qualquer número de clientes
- **Interface web**: mural de câmeras (cinco leiautes), eventos, linha do
  tempo multicâmera sincronizada com exportação, configurações da câmera
  descobertas do próprio aparelho (PTZ, zoom, luzes, sirene…), **vários
  idiomas** (incluindo português), aplicados ao vivo
- **Câmeras a bateria** (Argus etc.) autodetectadas e amigáveis ao sono
- **Gravação**: clipes de eventos + 24/7, armazenamento em camadas,
  criptografia em repouso, alertas por e-mail e navegador
- **Home Assistant / MQTT**: um dispositivo por câmera, sem YAML
- **Descrições de eventos por IA** (beta) via um LLM com visão, local ou hospedado

## Início rápido (Docker)

```bash
docker pull ghcr.io/borexola/neolink.net:latest
mkdir -p config
curl -o config/config.json https://raw.githubusercontent.com/borexola/neolink.net/main/src/Neolink.Server/config.example.json
# Edite config.json: nomes das câmeras, endereços IP e credenciais (as mesmas do aplicativo Reolink)

docker run -d --name neolink --restart unless-stopped \
    -p 8654:8654 -p 8655:8655 \
    -e TZ=America/Sao_Paulo \
    -v "$PWD/config:/config" \
    ghcr.io/borexola/neolink.net:latest
```

Interface web em `http://<host>:8655`, fluxos RTSP em
`rtsp://<host>:8654/<NomeDaCâmera>`. Também há um **complemento para o Home
Assistant** — veja a documentação em inglês.

O idioma da interface é escolhido na primeira inicialização (diálogo "Proteja
este servidor") ou em ⚙ → Idioma, e vale na hora — sem reiniciar e sem
recarregar.

## Documentação completa

A documentação detalhada (configuração, Home Assistant, câmeras a bateria,
armazenamento em camadas, aplicativo de desktop para Windows…) está no
[README em inglês](../../README.md) e na pasta [docs/](../).
