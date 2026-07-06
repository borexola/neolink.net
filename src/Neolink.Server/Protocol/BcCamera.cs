using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Xml.Linq;
using Neolink.Bc;
using Neolink.Bc.Xml;

namespace Neolink.Protocol;

public enum StreamKind
{
    Main,
    Sub,
    Extern,
}

public sealed class AuthFailedException : Exception
{
    public AuthFailedException(string msg) : base(msg) { }
}

/// <summary>High-level camera operations: login, video streaming, ping, control commands.</summary>
public sealed class BcCamera : IBcCamera
{
    public static readonly TimeSpan RxTimeout = TimeSpan.FromSeconds(15);

    private readonly BcConnection _conn;
    private readonly byte _channelId;
    private int _messageNum = -1;

    public byte ChannelId => _channelId;
    public DeviceInfoXml? DeviceInfo { get; private set; }

    private BcCamera(BcConnection conn, byte channelId)
    {
        _conn = conn;
        _channelId = channelId;
    }

    public static async Task<BcCamera> ConnectAsync(string host, int port, byte channelId, CancellationToken ct)
    {
        var conn = await BcConnection.ConnectAsync(host, port, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return new BcCamera(conn, channelId);
    }

    private ushort NewMessageNum() => (ushort)Interlocked.Increment(ref _messageNum);

    /// <summary>
    /// Login flow: send a header-only legacy "login upgrade" advertising AES support,
    /// receive the negotiated encryption scheme + nonce, then send a modern XML login
    /// with nonce-salted MD5 credentials. On AES cameras, derive the session key.
    /// </summary>
    public async Task LoginAsync(string username, string? password, CancellationToken ct)
    {
        using var sub = _conn.Subscribe(BcConstants.MsgIdLogin);

        // Header-only "login upgrade": no legacy username/password body. The camera
        // replies with the encryption scheme it will use and a login nonce.
        var legacy = new BcMessage
        {
            Meta = new BcMeta
            {
                MsgId = BcConstants.MsgIdLogin,
                ChannelId = _channelId,
                MsgNum = NewMessageNum(),
                StreamType = 0,
                ResponseCode = BcConstants.LegacyUpgradeAes,
                Class = BcConstants.ClassLegacy,
            },
        };
        await _conn.SendAsync(legacy, ct).ConfigureAwait(false);

        var reply = await sub.ReceiveAsync(RxTimeout, ct).ConfigureAwait(false);
        var nonce = reply.Xml?.Encryption?.Nonce
            ?? throw new BcProtocolException("Expected an Encryption message in the login reply");

        // The reply's response-code low byte is the negotiated scheme:
        // 0x00 = none, 0x01 = BCEncrypt, anything else (e.g. 0x12) = AES.
        int negotiated = reply.Meta.ResponseCode & 0xff;

        var modernPassword = password ?? "";
        var modern = BcMessage.FromXml(
            new BcMeta
            {
                MsgId = BcConstants.MsgIdLogin,
                ChannelId = _channelId,
                MsgNum = NewMessageNum(),
                StreamType = 0,
                ResponseCode = 0,
                Class = BcConstants.ClassModern,
            },
            new BcXmlBody
            {
                LoginUser = new LoginUserXml
                {
                    UserName = Md5Utils.Md5String31(username + nonce, zeroLast: false),
                    Password = Md5Utils.Md5String31(modernPassword + nonce, zeroLast: false),
                    UserVer = 1,
                },
                LoginNet = new LoginNetXml { Type = "LAN", UdpPort = 0 },
            });
        await _conn.SendAsync(modern, ct).ConfigureAwait(false);

        var modernReply = await sub.ReceiveAsync(RxTimeout, ct).ConfigureAwait(false);
        if (modernReply.Xml?.DeviceInfo != null)
        {
            DeviceInfo = modernReply.Xml.DeviceInfo;
        }
        else if (modernReply.IsEmptyModern)
        {
            throw new AuthFailedException("Camera rejected the credentials");
        }
        // else: some cameras reply with other XML; treat as success if response code is 200
        else if (modernReply.Meta.ResponseCode != 200)
        {
            throw new BcProtocolException($"Unexpected login reply (response code {modernReply.Meta.ResponseCode})");
        }

        // Now that the handshake is complete and the nonce is known, switch the
        // connection to AES for all subsequent messages if the camera negotiated it.
        // 0x02 = AES on control XML only; anything higher (e.g. 0x12) = FullAes,
        // where the media stream is encrypted too.
        if (negotiated != 0x00 && negotiated != 0x01)
        {
            var kind = negotiated == 0x02 ? EncryptionKind.Aes : EncryptionKind.FullAes;
            _conn.Encryption.Set(kind, Md5Utils.MakeAesKey(nonce, password ?? ""));
        }
    }

    /// <summary>
    /// Requests the video stream and pumps the raw binary sub-stream chunks into
    /// <paramref name="binaryOut"/> until cancelled or the connection drops.
    /// </summary>
    public async Task StartVideoAsync(StreamKind stream, ChannelWriter<byte[]> binaryOut, CancellationToken ct)
    {
        using var sub = _conn.Subscribe(BcConstants.MsgIdVideo);

        // Stream codes/handles as used by the official clients
        byte streamCode = stream == StreamKind.Sub ? (byte)1 : (byte)0;
        uint handle = stream switch
        {
            StreamKind.Main => 0u,
            StreamKind.Sub => 256u,
            StreamKind.Extern => 1024u,
            _ => 0u,
        };
        string streamName = stream switch
        {
            StreamKind.Main => "mainStream",
            StreamKind.Sub => "subStream",
            StreamKind.Extern => "externStream",
            _ => "mainStream",
        };

        var startVideo = BcMessage.FromXml(
            new BcMeta
            {
                MsgId = BcConstants.MsgIdVideo,
                ChannelId = _channelId,
                MsgNum = NewMessageNum(),
                StreamType = streamCode,
                ResponseCode = 0,
                Class = BcConstants.ClassModern,
            },
            new BcXmlBody
            {
                Preview = new PreviewXml
                {
                    ChannelId = _channelId,
                    Handle = handle,
                    StreamType = streamName,
                },
            });
        await _conn.SendAsync(startVideo, ct).ConfigureAwait(false);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                BcMessage msg;
                try
                {
                    msg = await sub.ReceiveAsync(RxTimeout, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new IOException("Video stream stalled (no data for 15s)");
                }
                if (msg.Binary is { Length: > 0 } bin)
                    await binaryOut.WriteAsync(bin, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            binaryOut.TryComplete();
        }
    }

    /// <summary>
    /// One-shot control command: subscribe to the message ID, send, await the reply.
    /// Runs safely alongside an active video stream (the connection routes replies
    /// by message ID), but concurrent commands with the SAME ID are not allowed —
    /// callers serialize (see CameraControl).
    /// </summary>
    public async Task<BcMessage?> SendCommandAsync(uint msgId, BcXmlBody? xml = null, ExtensionXml? extension = null,
        TimeSpan? replyTimeout = null, bool tolerateNoReply = false, CancellationToken ct = default)
    {
        using var sub = _conn.Subscribe(msgId);
        var msg = new BcMessage
        {
            Meta = new BcMeta
            {
                MsgId = msgId,
                ChannelId = _channelId,
                MsgNum = NewMessageNum(),
                StreamType = 0,
                ResponseCode = 0,
                Class = BcConstants.ClassModern,
            },
            Extension = extension,
            Xml = xml,
        };
        await _conn.SendAsync(msg, ct).ConfigureAwait(false);

        try
        {
            var reply = await sub.ReceiveAsync(replyTimeout ?? RxTimeout, ct).ConfigureAwait(false);
            if (reply.Meta.ResponseCode != 200)
                throw new CameraCommandException(msgId, reply.Meta.ResponseCode);
            return reply;
        }
        catch (TimeoutException) when (tolerateNoReply)
        {
            // Some cameras never acknowledge accepted set commands.
            return null;
        }
    }

    public async Task WatchMotionAsync(Action<MotionPush> onEvent, CancellationToken ct)
    {
        // Subscribe BEFORE opting in so no push can slip past between the two.
        using var sub = _conn.Subscribe(BcConstants.MsgIdMotion);

        // Opt in to alarm pushes. Some cameras ack the request, some stay silent
        // and just start pushing — tolerate both.
        await SendCommandAsync(BcConstants.MsgIdMotionRequest,
            extension: new ExtensionXml { ChannelId = _channelId },
            replyTimeout: TimeSpan.FromSeconds(5), tolerateNoReply: true, ct: ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            BcMessage msg;
            try
            {
                // Pushes are sporadic by nature: no read timeout. A dead connection
                // is noticed by the video stream, which tears everything down.
                msg = await sub.Messages.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                return; // connection closed
            }

            var list = msg.Xml?.RawElement("AlarmEventList");
            if (list == null) continue;
            foreach (var ev in list.Elements("AlarmEvent"))
            {
                if (uint.TryParse(ev.Element("channelId")?.Value.Trim(), out var ch) && ch != _channelId)
                    continue;
                var status = ev.Element("status")?.Value.Trim() ?? "";
                // AItype variants seen in the wild: repeated elements, comma lists,
                // "none" placeholders, and differing capitalization.
                var aiTypes = ev.Elements()
                    .Where(e => e.Name.LocalName.Equals("AItype", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(e => e.Value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    .Select(t => t.Trim().ToLowerInvariant())
                    .Where(t => t is not ("" or "none"))
                    .Distinct()
                    .ToList();
                onEvent(new MotionPush(status, aiTypes));
            }
        }
    }

    public async Task<byte[]?> SnapAsync(CancellationToken ct)
    {
        using var sub = _conn.Subscribe(BcConstants.MsgIdSnap);

        var body = BcXmlBody.FromRaw(new XElement("Snap",
            new XAttribute("version", BcXmlBody.XmlVersion),
            new XElement("channelId", _channelId),
            new XElement("logicChannel", _channelId),
            new XElement("time", 0),
            new XElement("fullFrame", 0),
            new XElement("streamType", "subStream")));
        var req = new BcMessage
        {
            Meta = new BcMeta
            {
                MsgId = BcConstants.MsgIdSnap,
                ChannelId = _channelId,
                MsgNum = NewMessageNum(),
                StreamType = 0,
                ResponseCode = 0,
                Class = BcConstants.ClassModern,
            },
            Extension = new ExtensionXml { ChannelId = _channelId },
            Xml = body,
        };
        await _conn.SendAsync(req, ct).ConfigureAwait(false);

        // The JPEG may span several messages: the first reply carries a <Snap>
        // with pictureSize, the image bytes arrive as binary payloads until
        // that many bytes have been received.
        int expected = -1;
        using var jpeg = new MemoryStream();
        var perMessageTimeout = TimeSpan.FromSeconds(8);
        while (true)
        {
            var reply = await sub.ReceiveAsync(perMessageTimeout, ct).ConfigureAwait(false);
            if (reply.Meta.ResponseCode != 200)
                throw new CameraCommandException(BcConstants.MsgIdSnap, reply.Meta.ResponseCode);
            if (reply.Xml?.RawElement("Snap") is { } snap
                && int.TryParse(snap.Element("pictureSize")?.Value.Trim(), out var size) && size > 0)
                expected = size;
            if (reply.Binary is { Length: > 0 } bin)
                jpeg.Write(bin, 0, bin.Length);
            if (expected >= 0 && jpeg.Length >= expected)
                return jpeg.ToArray();
            if (expected < 0 && jpeg.Length > 0)
                return jpeg.ToArray(); // no size advertised: single-message JPEG
        }
    }

    public async Task PingAsync(CancellationToken ct)
    {
        using var sub = _conn.Subscribe(BcConstants.MsgIdPing);
        var ping = BcMessage.HeaderOnly(new BcMeta
        {
            MsgId = BcConstants.MsgIdPing,
            ChannelId = _channelId,
            MsgNum = NewMessageNum(),
            StreamType = 0,
            ResponseCode = 0,
            Class = BcConstants.ClassModern,
        });
        await _conn.SendAsync(ping, ct).ConfigureAwait(false);
        await sub.ReceiveAsync(RxTimeout, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _conn.DisposeAsync();
}

public static class Md5Utils
{
    /// <summary>
    /// Reolink's odd MD5 mangling: uppercase hex MD5 truncated to 31 chars,
    /// with either a trailing NUL (legacy fields, 32 bytes total) or nothing (XML fields).
    /// </summary>
    public static string Md5String31(string input, bool zeroLast)
    {
        var hex = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))); // uppercase, 32 chars
        var truncated = hex[..31];
        return zeroLast ? truncated + "\0" : truncated;
    }

    /// <summary>AES key = first 16 bytes of the uppercase hex MD5 of "{nonce}-{password}".</summary>
    public static byte[] MakeAesKey(string nonce, string password)
    {
        var hex = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{nonce}-{password}")));
        return Encoding.ASCII.GetBytes(hex[..16]);
    }
}
