#!/usr/bin/env python3
"""
A fake Baichuan camera for end-to-end testing of Neolink.NET without hardware.

Speaks just enough of the BC protocol (BCEncrypt scheme):
  - legacy login  -> encryption reply (nonce)
  - modern login  -> DeviceInfo reply
  - preview (msg 3) -> binary-mode extension + looping BcMedia stream
  - ping (msg 93) -> 200

Usage: fake_camera.py <media-sample-dir> [port]
where media-sample-dir contains video_stream_swan_*.raw (from the Rust repo).
"""
import glob
import os
import socket
import struct
import sys
import threading
import time

XML_KEY = [0x1F, 0x2D, 0x3C, 0x4B, 0x5A, 0x69, 0x78, 0xFF]


def bc_xor(offset, data):
    return bytes(b ^ XML_KEY[(offset + i) % 8] ^ (offset & 0xFF) for i, b in enumerate(data))


def header(msg_id, body_len, msg_num, response_code, cls, channel=0, stream_type=0, payload_offset=None):
    h = struct.pack('<III BB H HH', 0x0abcdef0, msg_id, body_len, channel, stream_type,
                    msg_num, response_code, cls)
    if payload_offset is not None:
        h += struct.pack('<I', payload_offset)
    return h


def recv_exact(sock, n):
    buf = b''
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise ConnectionError('client gone')
        buf += chunk
    return buf


def read_msg(sock):
    head = recv_exact(sock, 20)
    magic, msg_id, body_len, channel, stream_type, msg_num, response_code, cls = \
        struct.unpack('<III BB H HH', head)
    assert magic == 0x0abcdef0, hex(magic)
    payload_offset = None
    if cls in (0x6414, 0x0000):
        payload_offset = struct.unpack('<I', recv_exact(sock, 4))[0]
    body = recv_exact(sock, body_len) if body_len else b''
    return msg_id, msg_num, cls, body


ENCRYPTION_XML = (b'<?xml version="1.0" encoding="UTF-8" ?>\n<body>\n'
                  b'<Encryption version="1.1">\n<type>md5</type>\n'
                  b'<nonce>9E6D1FCB9E69846D</nonce>\n</Encryption>\n</body>\n')

DEVICE_INFO_XML = (b'<?xml version="1.0" encoding="UTF-8" ?>\n<body>\n'
                   b'<DeviceInfo version="1.1">\n<resolution>\n'
                   b'<resolutionName>2560*1440</resolutionName>\n'
                   b'<width>2560</width>\n<height>1440</height>\n'
                   b'</resolution>\n</DeviceInfo>\n</body>\n')

BINARY_EXT_XML = (b'<?xml version="1.0" encoding="UTF-8" ?>\n'
                  b'<Extension version="1.1">\n<binaryData>1</binaryData>\n</Extension>\n')


def stream_media(sock, lock, msg_num, media, stop):
    """Send the media loop as msg 3 binary payload packets."""
    first = True
    while not stop.is_set():
        pos = 0
        while pos < len(media) and not stop.is_set():
            chunk = media[pos:pos + 16384]
            pos += len(chunk)
            if first:
                ext = bc_xor(0, BINARY_EXT_XML)
                body = ext + chunk
                head = header(3, len(body), msg_num, 200, 0x6414, payload_offset=len(ext))
                first = False
            else:
                head = header(3, len(chunk), msg_num, 200, 0x6414, payload_offset=0)
                body = chunk
            with lock:
                try:
                    sock.sendall(head + body)
                except OSError:
                    return
            time.sleep(0.05)  # pace roughly like a real camera


def handle_client(sock):
    lock = threading.Lock()
    stop = threading.Event()
    print('[fake-cam] client connected')
    try:
        while True:
            msg_id, msg_num, cls, body = read_msg(sock)
            if msg_id == 1 and cls == 0x6514:
                print('[fake-cam] legacy login -> nonce reply')
                payload = bc_xor(0, ENCRYPTION_XML)
                with lock:
                    sock.sendall(header(1, len(payload), msg_num, 0xdd01, 0x6614) + payload)
            elif msg_id == 1:
                print('[fake-cam] modern login -> device info')
                payload = bc_xor(0, DEVICE_INFO_XML)
                with lock:
                    sock.sendall(header(1, len(payload), msg_num, 200, 0x0000, payload_offset=0) + payload)
            elif msg_id == 3:
                print('[fake-cam] preview request -> streaming media')
                t = threading.Thread(target=stream_media, args=(sock, lock, msg_num, MEDIA, stop), daemon=True)
                t.start()
            elif msg_id == 93:
                with lock:
                    sock.sendall(header(93, 0, msg_num, 200, 0x0000, payload_offset=0))
            else:
                print(f'[fake-cam] ignoring msg {msg_id}')
    except (ConnectionError, OSError):
        print('[fake-cam] client disconnected')
    finally:
        stop.set()
        sock.close()


def main():
    sample_dir = sys.argv[1]
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 9000
    global MEDIA
    files = sorted(glob.glob(os.path.join(sample_dir, 'video_stream_swan_*.raw')))
    assert files, 'no sample files found'
    MEDIA = b''.join(open(f, 'rb').read() for f in files)
    print(f'[fake-cam] loaded {len(MEDIA)} bytes of media from {len(files)} files')

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(('127.0.0.1', port))
    srv.listen(4)
    print(f'[fake-cam] listening on 127.0.0.1:{port}')
    while True:
        client, _ = srv.accept()
        threading.Thread(target=handle_client, args=(client,), daemon=True).start()


if __name__ == '__main__':
    main()
