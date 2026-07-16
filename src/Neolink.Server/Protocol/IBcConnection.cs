// Copyright (c) 2026 Oluwabori Olaleye
// Licensed under the GNU Affero General Public License v3.0; see the LICENSE file
// in the repository root.
using Neolink.Bc;

namespace Neolink.Protocol;

/// <summary>
/// The transport surface <see cref="BcCamera"/> needs: subscribe to inbound
/// messages by id, send a message, the negotiated encryption state, and disposal.
/// Both the TCP <see cref="BcConnection"/> and the UDP <c>BcUdpConnection</c>
/// implement it, so every camera operation (login, streaming, control) is written
/// once and runs unchanged over either transport — the BC messages are identical;
/// only the bytes-on-the-wire carrier differs.
/// </summary>
public interface IBcConnection : IAsyncDisposable
{
    EncryptionState Encryption { get; }
    BcSubscription Subscribe(uint msgId);
    Task SendAsync(BcMessage msg, CancellationToken ct);
}
