using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using System.Linq;
using System.Collections.Specialized;

namespace UDP_Networking {
    class Server {

        public Server(int port, string ip_address = "127.0.0.1") 
            : this(new IPEndPoint(IPAddress.Parse(ip_address), port)) {}

        public Server(IPEndPoint endpoint) {
            _running = false;
            _server_info = endpoint;
            _connected_to = null;
            _connection_timer = new Stopwatch();
        }

        public void Start() {
            if (_running) {
                PrintServerMessage("Can't start server: Already running.");
                return;
            }    
            PrintServerMessage("Starting up server");
            _udp_client = new UdpClient(_server_info);
            _running = true;
            _messages_received = new Queue<byte[]>(32);
            _last_sent_packet_id = 0;
            _most_recent_received_packet_id = 0;
            _packet_ids_received = new FixedQueue<ushort>(SIZE_OF_PACKET_HISTORY);
            _acks_received = new FixedQueue<ushort>(SIZE_OF_ACKS_RECEIVED);
            var main_thread = new Thread(MainServerThread);
            var timeout_counter = new Thread(TimeoutCounterThread);
            var pulse_thread = new Thread(PulseThread);
            main_thread.Start();
            timeout_counter.Start();
            pulse_thread.Start();
        }

        public void Close() {
            if (!_running) {
                PrintServerMessage("Can't close server: Already closed.");
                return;
            }
            PrintServerMessage("Closing server");
            _running = false;
            _udp_client.Close();
        }

        public void SendMessage(byte[] message, IPEndPoint to, bool reliable = false, int times_to_try_resending = DEFAULT_RESEND_TIMES) {
            if (!_running) {
                return;
            }
            try {
                lock (_remote_packet_history_lock) {
                    if (reliable && times_to_try_resending > 0) {
                        var message_copy = message.ToList().ToArray();
                        var reliability_check = new Thread(() => { MessageReliabilityThread(_last_sent_packet_id, message_copy, times_to_try_resending); });
                        reliability_check.Start();
                    }
                    AddHeaderToPacket(ref message, GetAckBitfield());
                    _last_sent_packet_id++;
                }
                _udp_client.Send(message, message.Length, to);
            }
            catch (SocketException e) {
                switch (e.SocketErrorCode) {
                    case SocketError.NotConnected: { PrintServerMessage("Can't send, remote host not connected."); } break;
                    default: { PrintServerMessage(string.Format("Sender Unprepared for SocketException with error code '{0}'", e.SocketErrorCode)); } break;
                }
            }
        }

        public void SendMessage(string message, IPEndPoint to, bool reliable = false, int times_to_try_resending = DEFAULT_RESEND_TIMES) {
            var bytes = Encoding.ASCII.GetBytes(message);
            SendMessage(bytes, to, reliable, times_to_try_resending);
        }

        public bool HasMessage() {
            lock (_message_queue_lock) 
            {
                return _messages_received.Count != 0;
            }
        }

        public byte[] GetNextMessage() {
            lock (_message_queue_lock) 
            {
                var message = _messages_received.Dequeue();
                return message;
            }
        }

        public bool HasConnection() {
            return _connected_to != null;
        }

        // --- PRIVATE METHODS AND MEMBERS --- //

        private void MainServerThread() {
            PrintServerMessage("Listening for UDP packets . . .");
            while (_running) {
                try {
                    var received_from = new IPEndPoint(IPAddress.Any, 0);
                    var received_bytes = _udp_client.Receive(ref received_from); // this is a blocking call, will only continue when data is received
                    if (!IsValidProtocol(received_bytes)) continue;
                    lock (_connection_timer_lock) 
                    {
                        if (!HasConnection()) {
                            EstablishConnection(received_from);
                            lock (_remote_packet_history_lock) 
                            {
                                _most_recent_received_packet_id = GetSequenceNumberFromPacket(received_bytes);
                            }
                        }
                        if (_connected_to.Equals(received_from)) {
                            RestartConnectionTimer();
                            lock (_remote_packet_history_lock) 
                            {
                                var packet_sequence = GetSequenceNumberFromPacket(received_bytes);
                                UpdateReceivedPacketIDs(packet_sequence);
                            }
                            lock (_acks_received_lock) 
                            {
                                UpdateAcksReceived(GetBitfieldFromPacket(received_bytes), GetHighestAckFromPacket(received_bytes));
                            }
                            RemoveHeaderFromPacket(ref received_bytes);
                            if (IsPulse(received_bytes)) continue;
                            var store_bytes_thread = new Thread( () => StoreReceivedBytes(received_bytes, received_from) );
                            store_bytes_thread.Start();
                        }
                        else {
                            continue;
                        }
                    }
                }
                catch (SocketException e) {
                    switch (e.SocketErrorCode) {
                        case SocketError.ConnectionReset: { PrintServerMessage("Remote host offline. Sent message not received."); } break;
                        case SocketError.Interrupted: { PrintServerMessage("Closed while Socket.Receive was listening."); } break;
                        default: { PrintServerMessage(string.Format("Unprepared for SocketException with error code '{0}'", e.SocketErrorCode)); } break;
                    }
                }
            }
            PrintServerMessage("Stopped listening for UDP packets.");
        }

        private void TimeoutCounterThread() {
            while (_running) {
                if (!HasConnection()) continue;
                lock (_connection_timer_lock) 
                {
                    if (_connection_timer.Elapsed.Seconds >= CONNECTION_TIMEOUT_LIMIT_SEC) {
                        TimeoutDropConnection(_connected_to);
                    }
                }
                Thread.Sleep(MS_PER_TIMEOUT_CHECK);
            }
        }

        private void PulseThread() {
            while(_running) {
                if (!HasConnection()) continue;
                SendMessage(PULSE, _connected_to);
                Thread.Sleep(MS_PER_PULSE);
            }
        }

        private void MessageReliabilityThread(ushort packet_id, byte[] message, int times_to_try_resending = DEFAULT_RESEND_TIMES) {
            Thread.Sleep(RELIABILITY_CHECK_WAITING_TIME_MS);
            if (!HasConnection()) return;
            lock (_acks_received_lock) 
            {
                if (!_acks_received.Contains(packet_id)) {
                    PrintServerMessage(string.Format("Resending packet with content '{0}'", Encoding.ASCII.GetString(message)));
                    SendMessage(message, _connected_to, true, --times_to_try_resending);
                }
            }
        }

        private void PrintServerMessage(string message) {
            Console.WriteLine(string.Format("[{0}:{1}] {2}", _server_info.Address, _server_info.Port, message));
        }

        private void EstablishConnection(IPEndPoint new_connection) {
            PrintServerMessage(string.Format("Establishing connection with {0}:{1}", new_connection.Address, new_connection.Port));
            _connected_to = new_connection;
            RestartConnectionTimer();
        }

        private void RestartConnectionTimer() {
            _connection_timer.Reset();
            _connection_timer.Start();
        }

        private void TimeoutDropConnection(IPEndPoint connection) {
            PrintServerMessage(string.Format("Dropping connection with {0}:{1} - Timed out at {2} sec.",
                _connected_to.Address, _connected_to.Port, CONNECTION_TIMEOUT_LIMIT_SEC));
            _connected_to = null;
            _connection_timer.Stop();
        }

        private void StoreReceivedBytes(byte[] bytes, IPEndPoint from) {
            lock (_message_queue_lock) 
            {
                _messages_received.Enqueue(bytes);
            }
        }

        private bool IsPulse(byte[] bytes) {
            return bytes.SequenceEqual(PULSE);
        }

        private void UpdateReceivedPacketIDs(ushort packet_id) {
            var old_p = _most_recent_received_packet_id;
            var new_p = packet_id;
            var incoming_is_more_recent =
                (new_p > old_p) && (new_p - old_p <= ushort.MaxValue / 2) ||
                (old_p > new_p) && (old_p - new_p > ushort.MaxValue / 2);

            if (incoming_is_more_recent) {
                _most_recent_received_packet_id = packet_id;
            }

            if (!_packet_ids_received.Contains(packet_id)) {
                _packet_ids_received.Enqueue(packet_id);
            }
        }

        private void AddHeaderToPacket(ref byte[] bytes, BitVector32 bitfield) {
            var protocol = BitConverter.GetBytes(PROTOCOL_ID);
            var packet_sequence_number = BitConverter.GetBytes(_last_sent_packet_id);
            var ack = BitConverter.GetBytes(_most_recent_received_packet_id);
            var ack_bitfield = BitConverter.GetBytes(bitfield.Data);

            var message_with_header = new List<byte>();
            message_with_header.AddRange(protocol);
            message_with_header.AddRange(packet_sequence_number);
            message_with_header.AddRange(ack);
            message_with_header.AddRange(ack_bitfield);
            message_with_header.AddRange(bytes);

            bytes = message_with_header.ToArray();
        }

        private void RemoveHeaderFromPacket(ref byte[] bytes) {
            var HEADER_LENGTH_IN_BYTES = GetPacketHeaderLength();
            var MESSAGE_LENGTH_IN_BYTES = bytes.Length - HEADER_LENGTH_IN_BYTES;

            var message_without_header = new List<byte>(bytes);
            bytes = message_without_header.GetRange(HEADER_LENGTH_IN_BYTES, MESSAGE_LENGTH_IN_BYTES).ToArray();
        }

        private bool IsValidProtocol(byte[] bytes) {
            return BitConverter.ToUInt16(bytes, 0) == PROTOCOL_ID;
        }

        private ushort GetSequenceNumberFromPacket(byte[] bytes) {
            return BitConverter.ToUInt16(bytes, 2);
        }

        private ushort GetHighestAckFromPacket(byte[] bytes) {
            return BitConverter.ToUInt16(bytes, 4);
        }

        private BitVector32 GetBitfieldFromPacket(byte[] bytes) {
            var bits_as_int = BitConverter.ToInt32(bytes, 6);
            return new BitVector32(bits_as_int);
        }

        private int GetPacketHeaderLength() {
            // packet header = protocol (ushort), packet sequence (ushort), ack (ushort), ack bitfield (BitVector32)
            return sizeof(ushort) + sizeof(ushort) + sizeof(ushort) + 4;
        }

        private void UpdateAcksReceived(BitVector32 acks, ushort upper_bound) {
            var lower_bound = (ushort)(upper_bound - (SIZE_OF_PACKET_HISTORY - 1));
            if (lower_bound > upper_bound) {
                var cutoff = (ushort)(ushort.MaxValue - lower_bound);
                for (ushort bit = 0; bit < SIZE_OF_PACKET_HISTORY; bit++) {
                    if (acks[1 << bit]) {
                        var packet_id = bit < cutoff ? (ushort) (lower_bound + bit) : bit;
                        if (!_acks_received.Contains(packet_id)) {
                            _acks_received.Enqueue(packet_id);
                        }
                    }
                }
            }
            else {
                for (ushort bit = 0; bit < SIZE_OF_PACKET_HISTORY; bit++) {
                    if (acks[1 << bit]) {
                        var packet_id = (ushort) (lower_bound + bit);
                        if (!_acks_received.Contains(packet_id)) {
                            _acks_received.Enqueue(packet_id);
                        }
                    }
                }
            }
        }

        private BitVector32 GetAckBitfield() {
            var upper_bound = _most_recent_received_packet_id;
            var lower_bound = (ushort)(upper_bound - (SIZE_OF_PACKET_HISTORY - 1));

            var bitfield = new BitVector32(0);
            if (lower_bound > upper_bound) {
                foreach (var packet_id in _packet_ids_received) {
                    if (lower_bound <= packet_id || packet_id <= upper_bound) {
                        var bit_position = packet_id >= lower_bound ? (packet_id - lower_bound) : (SIZE_OF_PACKET_HISTORY - 1) - (upper_bound - packet_id);
                        bitfield[1 << bit_position] = true;
                    }
                }
            }
            else {
                foreach (var packet_id in _packet_ids_received) {
                    if (lower_bound <= packet_id && packet_id <= upper_bound) {
                        var bit_position = packet_id - lower_bound;
                        bitfield[1 << bit_position] = true;
                    }
                }
            }

            return bitfield;
        }

        // object state
        private bool _running;
        private UdpClient _udp_client;
        private IPEndPoint _server_info;
        private IPEndPoint _connected_to;
        private Stopwatch _connection_timer;

        private Queue<byte[]> _messages_received;
        private FixedQueue<ushort> _packet_ids_received;
        private FixedQueue<ushort> _acks_received;

        private ushort _last_sent_packet_id;
        private ushort _most_recent_received_packet_id;

        // constants
        private const ushort SIZE_OF_PACKET_HISTORY = 32;
        private const ushort SIZE_OF_ACKS_RECEIVED = 50;
        private const int DEFAULT_RESEND_TIMES = 10;
        private const int CONNECTION_TIMEOUT_LIMIT_SEC = 5;
        private const int RELIABILITY_CHECK_WAITING_TIME_MS = 1000;
        private const int MS_PER_TIMEOUT_CHECK = 500;
        private const int MS_PER_PULSE = 100;
        private readonly byte[] PULSE = { 9, 4, 1, 5 };
        private const ushort PROTOCOL_ID = 30321;

        // thread locks
        private object _connection_timer_lock = new object();
        private object _message_queue_lock = new object();
        private object _remote_packet_history_lock = new object();
        private object _acks_received_lock = new object();

    }
}
