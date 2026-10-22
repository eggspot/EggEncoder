using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EggEncoder.Live.Rtmp
{
    /// <summary>
    /// The RTMP handshake (Adobe RTMP specification 1.0, section 5.2) as a state machine over bytes,
    /// for either side of a connection. It never touches a socket: the caller sends what
    /// <see cref="Start"/> returns, then repeatedly reads exactly <see cref="BytesExpected"/> bytes from
    /// the peer, passes them to <see cref="Process"/> and sends what it returns, until
    /// <see cref="IsComplete"/>:
    /// <code>
    /// send(handshake.Start());
    /// while (!handshake.IsComplete)
    /// {
    ///     send(handshake.Process(readExactly(handshake.BytesExpected)));
    /// }
    /// </code>
    /// </summary>
    /// <remarks>
    /// This is the simple handshake the specification describes: version byte 3, then 1536-byte
    /// packets of time, zero and random bytes, each side echoing the other's. Only version 3 is
    /// accepted; versions 6 and 8 (RTMPE, Adobe's encrypted variant) and every other value throw
    /// <see cref="RtmpProtocolException"/>. The echoes (C2, S2) are not compared with what was sent:
    /// publishers that speak the undocumented digest variant don't echo byte for byte, and nothing
    /// in RTMP depends on the echo being exact.
    /// </remarks>
    public sealed class RtmpHandshake
    {
        /// <summary>The only RTMP version this library speaks (the C0/S0 byte).</summary>
        public const byte Version = 3;

        /// <summary>The length of C1, S1, C2 and S2.</summary>
        public const int PacketLength = 1536;

        private enum State
        {
            NotStarted,
            AwaitingPeerPackets,
            AwaitingClientEcho,
            Complete
        }

        private readonly bool _isServer;
        private State _state = State.NotStarted;

        private RtmpHandshake(bool isServer)
        {
            _isServer = isServer;
        }

        /// <summary>The handshake for the side that accepted the connection (expects C0+C1, sends S0+S1+S2, expects C2).</summary>
        public static RtmpHandshake CreateServer() => new(isServer: true);

        /// <summary>The handshake for the side that opened the connection (sends C0+C1, expects S0+S1+S2, sends C2).</summary>
        public static RtmpHandshake CreateClient() => new(isServer: false);

        /// <summary>True once both sides have exchanged everything; chunked messages follow.</summary>
        public bool IsComplete => _state == State.Complete;

        /// <summary>How many bytes <see cref="Process"/> needs next; 0 before <see cref="Start"/> and once complete.</summary>
        public int BytesExpected => _state switch
        {
            State.AwaitingPeerPackets => _isServer ? 1 + PacketLength : 1 + (2 * PacketLength),
            State.AwaitingClientEcho => PacketLength,
            _ => 0
        };

        /// <summary>Starts the handshake and returns the bytes to send first: C0+C1 for a client, nothing for a server.</summary>
        /// <exception cref="InvalidOperationException">Already started.</exception>
        public byte[] Start()
        {
            if (_state != State.NotStarted)
            {
                throw new InvalidOperationException("The handshake has already started.");
            }

            _state = State.AwaitingPeerPackets;
            if (_isServer)
            {
                return [];
            }

            var c0c1 = new byte[1 + PacketLength];
            c0c1[0] = Version;
            FillPacket(c0c1.AsSpan(1));
            return c0c1;
        }

        /// <summary>
        /// Consumes exactly <see cref="BytesExpected"/> bytes from the peer and returns the bytes to send
        /// in reply (S0+S1+S2 for a server given C0+C1, C2 for a client given S0+S1+S2, nothing for a
        /// server given C2).
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="peerBytes"/> is not exactly <see cref="BytesExpected"/> long.</exception>
        /// <exception cref="InvalidOperationException">Not started yet, or already complete.</exception>
        /// <exception cref="RtmpProtocolException">The peer asked for a version other than 3.</exception>
        public byte[] Process(ReadOnlySpan<byte> peerBytes)
        {
            if (_state is State.NotStarted or State.Complete)
            {
                throw new InvalidOperationException(_state == State.NotStarted
                    ? "Call Start before Process."
                    : "The handshake is already complete.");
            }

            if (peerBytes.Length != BytesExpected)
            {
                throw new ArgumentException($"Expected exactly {BytesExpected} handshake bytes, got {peerBytes.Length}.", nameof(peerBytes));
            }

            if (_state == State.AwaitingClientEcho)
            {
                _state = State.Complete;
                return [];
            }

            var version = peerBytes[0];
            if (version != Version)
            {
                throw new RtmpProtocolException(version is 6 or 8
                    ? $"RTMP handshake: version {version} is RTMPE (encrypted RTMP), which is not supported"
                    : $"RTMP handshake: unsupported version {version}, only {Version} is spoken");
            }

            var peerPacket = peerBytes.Slice(1, PacketLength);
            if (_isServer)
            {
                // S0 + S1 + S2, where S2 echoes C1.
                var reply = new byte[1 + (2 * PacketLength)];
                reply[0] = Version;
                FillPacket(reply.AsSpan(1, PacketLength));
                peerPacket.CopyTo(reply.AsSpan(1 + PacketLength));
                _state = State.AwaitingClientEcho;
                return reply;
            }

            // C2 echoes S1; S2 (our C1 echoed back) is not checked, see the remarks.
            _state = State.Complete;
            return peerPacket.ToArray();
        }

        // Time (0 is allowed), four zero bytes, then random bytes.
        private static void FillPacket(Span<byte> packet)
        {
            BinaryPrimitives.WriteUInt32BigEndian(packet, 0);
            packet.Slice(4, 4).Clear();
            RandomNumberGenerator.Fill(packet[8..]);
        }
    }
}
