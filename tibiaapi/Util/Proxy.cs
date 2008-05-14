using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using Tibia.Objects;
using Tibia.Packets;
using System.IO;

namespace Tibia.Util
{
    public class Proxy
    {
        #region Variables
        /// <summary>
        /// Static client for checking open ports.
        /// </summary>
        private static TcpListener tcpScan;

        private Socket socketClient;

        private NetworkStream netStreamClient;
        private NetworkStream netStreamServer;
        private NetworkStream netStreamLogin;

        private TcpListener tcpClient;
        private TcpClient   tcpServer;
        private TcpClient   tcpLogin;

        private const string Localhost = "127.0.0.1";
        private byte[]       LocalhostBytes = new byte[] { 127, 0, 0, 1 };
        private const short  DefaultPort = 7171;

        private Client         client;
        private CharListPacket charList;
        private byte           selectedChar;
        private bool           connected = false;
        private bool           isLoggedIn = false;
        private int            loginDelay = 250;
        private short          localPort;
        public Queue<byte[]>  serverReceiveQueue = new Queue<byte[]>();
        private Queue<byte[]>  clientReceiveQueue = new Queue<byte[]>();
        private Queue<byte[]>  clientSendQueue = new Queue<byte[]>();
        private Queue<byte[]>  serverSendQueue = new Queue<byte[]>();
        private byte[]         dataServer = new byte[8192];
        private byte[]         dataClient = new byte[8192];
        private bool           writingToClient = false;
        private bool           writingToServer = false;
        private DateTime       lastServerWrite = DateTime.UtcNow;
        private PacketBuilder  partial;
        private int            partialRemaining = 0;

        private LoginServer[]  loginServers = new LoginServer[] {
            new LoginServer("login01.tibia.com", 7171),
            new LoginServer("login02.tibia.com", 7171),
            new LoginServer("login03.tibia.com", 7171),
            new LoginServer("login04.tibia.com", 7171),
            new LoginServer("login05.tibia.com", 7171),
            new LoginServer("tibia01.cipsoft.com", 7171),
            new LoginServer("tibia02.cipsoft.com", 7171),
            new LoginServer("tibia03.cipsoft.com", 7171),
            new LoginServer("tibia04.cipsoft.com", 7171),
            new LoginServer("tibia05.cipsoft.com", 7171)
        };
        #endregion

        #region Events
        /// <summary>
        /// A generic function prototype for packet events.
        /// </summary>
        /// <param name="packet">The unencrypted packet that was received.</param>
        /// <returns>true to continue forwarding the packet, false to drop the packet</returns>
        public delegate bool PacketListener(Packet packet);

        /// <summary>
        /// A function prototype for proxy notifications.
        /// </summary>
        /// <returns></returns>
        public delegate void ProxyNotification();

        /// <summary>
        /// Called when the client has logged in.
        /// </summary>
        public ProxyNotification OnLogIn;

        /// <summary>
        /// Called when the client has logged out.
        /// </summary>
        public ProxyNotification OnLogOut;

        /// <summary>
        /// Called when the client crashes.
        /// </summary>
        public ProxyNotification OnCrash;

        /// <summary>
        /// Called when a packet is received from the server.
        /// </summary>
        public PacketListener ReceivedPacketFromServer;

        /// <summary>
        /// Called when a packet is received from the client.
        /// </summary>
        public PacketListener ReceivedPacketFromClient;

        // Incoming
        public PacketListener ReceivedAnimatedTextPacket;
        public PacketListener ReceivedChannelListPacket;
        public PacketListener ReceivedChannelOpenPacket;
        public PacketListener ReceivedChatMessagePacket;
        public PacketListener ReceivedCreatureHealthPacket;
        public PacketListener ReceivedProjectilePacket;
        public PacketListener ReceivedStatusMessagePacket;
        public PacketListener ReceivedVipLoginPacket;
        public PacketListener ReceivedCreatureMovePacket;
        public PacketListener ReceivedTileAnimationPacket;
        public PacketListener ReceivedCreatureSquarePacket;
        public PacketListener ReceivedStatusUpdatePacket;
        public PacketListener ReceivedMapItemRemovePacket;
        public PacketListener ReceivedMapItemAddPacket;

        // Outgoing
        public PacketListener ReceivedPlayerSpeechPacket;

        #endregion

        #region Properties
        /// <summary>
        /// Returns true if the proxy is connected
        /// </summary>
        public bool Connected
        {
            get { return connected; }
        }

        /// <summary>
        /// Returns true if the client is logged in.
        /// </summary>
        public bool IsLoggedIn
        {
            get { return isLoggedIn; }
        }
        #endregion

        #region Constructors
        /// <summary>
        /// Default constructor, does nothing.
        /// </summary>
        public Proxy() { }

        /// <summary>
        /// Create a new proxy and start listening for the client to connect.
        /// </summary>
        /// <param name="c"></param>
        public Proxy(Client c) : this(c, new LoginServer(string.Empty, 0)) { }

        /// <summary>
        /// Create a new proxy that connects to the specified server and the default port (7171).
        /// </summary>
        /// <param name="c"></param>
        /// <param name="serverIP"></param>
        public Proxy(Client c, string serverIP) : this(c, serverIP, DefaultPort) { }

        /// <summary>
        /// Create a new proxy that connects to the specified server and port.
        /// </summary>
        /// <param name="c"></param>
        /// <param name="serverIP"></param>
        /// <param name="serverPort"></param>
        public Proxy(Client c, string serverIP, short serverPort) : this (c, new LoginServer(serverIP, serverPort)) { }

        /// <summary>
        /// Create a new proxy with the specified login server.
        /// </summary>
        /// <param name="c"></param>
        /// <param name="ls"></param>
        public Proxy(Client c, LoginServer ls)
        {
            client = c;
            c.UsingProxy = true;
            if (!ls.Server.Equals(string.Empty))
            {
                loginServers = new LoginServer[] { ls };
            }
            localPort = (short)GetFreePort();
            client.SetServer(Localhost, localPort);
            StartClientListener();
        }
        #endregion

        #region Startup
        /// <summary>
        /// Restart the proxy.
        /// </summary>
        public void Restart()
        {
            System.Threading.Thread.Sleep(500);
            StartClientListener();
        }

        private void StartClientListener()
        {
            tcpClient = new TcpListener(IPAddress.Any, localPort);
            tcpClient.Start();
            tcpClient.BeginAcceptSocket((AsyncCallback)ClientConnected, null);
        }

        private void ClientConnected(IAsyncResult ar)
        {
            socketClient = tcpClient.EndAcceptSocket(ar);

            if (socketClient.Connected)
            {
                netStreamClient = new NetworkStream(socketClient);

                //Connect the proxy to the login server.
                tcpLogin = new TcpClient(loginServers[0].Server, loginServers[0].Port);
                netStreamLogin = tcpLogin.GetStream();

                //Listen for the client to request the character list
                netStreamClient.BeginRead(dataClient, 0, dataClient.Length, ClientLoginReceived, null);
            }
        }

        private void ClientLoginReceived(IAsyncResult ar)
        {
            int bytesRead = netStreamClient.EndRead(ar);

            if (bytesRead > 0)
            {
                // Check whether this is a char list request or game server login
                if (dataClient[2] == (byte)PacketType.AddCreature)
                {
                    ConnectClientToGameWorld(bytesRead);
                }
                else if (dataClient[2] == (byte)PacketType.CharListLoginData)
                {
                    // Relay the login details to the Login Server
                    netStreamLogin.BeginWrite(dataClient, 0, bytesRead, null, null);

                    // Begin read for the character list
                    netStreamLogin.BeginRead(dataServer, 0, dataServer.Length, CharListReceived, null);
                }
            }
        }

        private void CharListReceived(IAsyncResult ar)
        {
            int bytesRead = netStreamLogin.EndRead(ar);

            if (bytesRead > 0)
            {
                // Process the character list
                ProcessCharListPacket(dataServer, bytesRead);

                // Send the modified char list to the client
                netStreamClient.BeginWrite(dataServer, 0, bytesRead, null, null);

                // Refresh the client listener because the client reconnects on a
                // different port with the game server
                RefreshClientListener();
            }
        }

        private void ProcessCharListPacket(byte[] data, int length)
        {
            byte[] packet = new byte[length];
            byte[] key = client.ReadBytes(Addresses.Client.XTeaKey, 16);

            Array.Copy(data, packet, length);
            packet = XTEA.Decrypt(packet, key);

            charList = new CharListPacket();
            charList.ParseData(packet, LocalhostBytes, BitConverter.GetBytes((short)localPort));

            packet = XTEA.Encrypt(charList.Data, key);
            Array.Copy(packet, data, length);
        }

        private void RefreshClientListener()
        {
            // Refresh the client listener
            tcpClient.Stop();
            tcpClient.Start();
            tcpClient.BeginAcceptSocket((AsyncCallback)ClientReconnected, null);
        }

        private void ClientReconnected(IAsyncResult ar)
        {
            socketClient = tcpClient.EndAcceptSocket(ar);

            if (socketClient.Connected)
            {
                // The client has successfully reconnected
                netStreamClient = new NetworkStream(socketClient);

                // Begint to read the game login packet from the client
                netStreamClient.BeginRead(dataClient, 0, dataClient.Length, ClientGameLoginReceived, null);
            }
        }

        private void ClientGameLoginReceived(IAsyncResult ar)
        {
            int bytesRead = netStreamClient.EndRead(ar);

            if (bytesRead > 0)
            {
                ConnectClientToGameWorld(bytesRead);
            }
        }

        private void ConnectClientToGameWorld(int bytesRead)
        {
            // Read the selection index from memory
            selectedChar = client.ReadByte(Addresses.Client.LoginSelectedChar);

            // Connect to the selected game world
            tcpServer = new TcpClient(charList.chars[selectedChar].worldIP, charList.chars[selectedChar].worldPort);
            netStreamServer = tcpServer.GetStream();

            // Begin to write the login data to the game server
            netStreamServer.BeginWrite(dataClient, 0, bytesRead, null, null);

            // Start asynchronous reading
            netStreamServer.BeginRead(dataServer, 0, dataServer.Length, (AsyncCallback)ReceiveFromServer, null);
            netStreamClient.BeginRead(dataClient, 0, dataClient.Length, (AsyncCallback)ReceiveFromClient, null);

            // The proxy is now connected
            connected = true;
        }
        #endregion

        #region Server -> Client
        private void ReceiveFromServer(IAsyncResult ar)
        {
            if (!netStreamServer.CanRead) return;
                
            int bytesRead = netStreamServer.EndRead(ar);
            if (bytesRead == 0) return;
            int offset = 0;

            while (bytesRead - offset > 0)
            {
                // Get the packet length
                int packetlength = BitConverter.ToInt16(dataServer, offset) + 2;

                // Parse the data into a single packet
                byte[] packet = new byte[packetlength];
                Array.Copy(dataServer, offset, packet, 0, packetlength);
                
                // Enqueue the packet for processing
                serverReceiveQueue.Enqueue(packet);

                offset += packetlength;
            }

            ProcessServerReceiveQueue();

            if (!netStreamServer.CanRead) return;

            netStreamServer.BeginRead(dataServer, 0, dataServer.Length, (AsyncCallback)ReceiveFromServer, null);
        }

        public void ProcessServerReceiveQueue()
        {
            if (serverReceiveQueue.Count > 0)
            {
                byte[] original = serverReceiveQueue.Dequeue();
                byte[] decrypted = DecryptPacket(original);

                int remaining = 0; // the bytes worth of logical packets left

                // Always call the default (if attached to)
                //if (ReceivedPacketFromServer != null)
                //    ReceivedPacketFromServer(new Packet(decrypted));

                // Is this a part of a larger packet?
                if (partialRemaining > 0)
                {
                    // Not sure if this works yet...
                    // Yes, tack it onto the end of the partial packet
                    partial.AddBytes(decrypted);

                    // Subtract from the remaining needed
                    partialRemaining -= decrypted.Length;
                    
                }
                else
                {
                    // No, create a new partial packet
                    partial = new PacketBuilder(decrypted);
                    remaining = partial.GetInt();
                    partialRemaining = remaining - (decrypted.Length - 2); // packet length - part we already have
                }

                // Do we have a complete packet now?
                if (partialRemaining == 0)
                {
                    int length = 0;
                    bool forward;

                    // Keep going until no more logical packets
                    while (remaining > 0)
                    {
                        // Process the packet
                        forward = RaiseIncomingEvents(decrypted, ref length);

                        // If packet not found in database, forward the rest
                        if (length == -1)
                        {
                            SendToClient(decrypted);
                            break;
                        }

                        length++;
                        if (forward)
                        {
                            if (ReceivedPacketFromServer != null)
                                ReceivedPacketFromServer(new Packet(Repackage(decrypted, 2, length)));

                            // Repackage it and send
                            SendToClient(Repackage(decrypted, 2, length));
                        }

                        // Subtract the amount that was parsed
                        remaining -= length;

                        // Repackage decrypted without the first logical packet
                        if (remaining > 0)
                            decrypted = Repackage(decrypted, length + 2);
                    }

                    // Start processing the queue
                    ProcessClientSendQueue();
                }
                // else, delay processing until the rest of the packet arrives

                if (serverReceiveQueue.Count > 0)
                    ProcessServerReceiveQueue();
            }
        }

        private byte[] Repackage(byte[] data)
        {
            return Repackage(data, 0);
        }

        private byte[] Repackage(byte[] data, int start)
        {
            return Repackage(data, start, data.Length - start);
        }

        private byte[] Repackage(byte[] data, int start, int length)
        {
            byte[] packaged = new byte[length + 2];
            Array.Copy(BitConverter.GetBytes((ushort)length), packaged, 2);
            Array.Copy(data, start, packaged, 2, length);
            return packaged;
        }

        private bool RaiseIncomingEvents(byte[] packet, ref int length)
        {
            length = -1;
            if (packet.Length < 3) return true;
            Packet p;
            PacketType type = (PacketType)packet[2];
            switch (type)
            {
                case PacketType.AnimatedText:
                    p = new AnimatedTextPacket(packet);
                    length = p.Index;
                    if (ReceivedAnimatedTextPacket != null)
                        return ReceivedAnimatedTextPacket(p);
                    break;
                case PacketType.ChatMessage:
                    p = new ChatMessagePacket(packet);
                    length = p.Index;
                    if (ReceivedChatMessagePacket != null)
                        return ReceivedChatMessagePacket(p);
                    break;
                case PacketType.StatusMessage:
                    p = new StatusMessagePacket(packet);
                    length = p.Index;
                    if (ReceivedStatusMessagePacket != null)
                        return ReceivedStatusMessagePacket(p);
                    break;
                case PacketType.Projectile:
                    p = new ProjectilePacket(packet);
                    length = p.Index;
                    if (ReceivedProjectilePacket != null)
                        return ReceivedProjectilePacket(p);
                    break;
                case PacketType.CreatureHealth:
                    p = new CreatureHealthPacket(packet);
                    length = p.Index;
                    if (ReceivedCreatureHealthPacket != null)
                        return ReceivedCreatureHealthPacket(p);
                    break;
                case PacketType.VipLogin:
                    p = new VipLoginPacket(packet);
                    length = p.Index;
                    if (ReceivedVipLoginPacket != null)
                        return ReceivedVipLoginPacket(p);
                    break;
                case PacketType.ChannelList:
                    p = new ChannelListPacket(packet);
                    length = p.Index;
                    if (ReceivedChannelListPacket != null)
                        return ReceivedChannelListPacket(p);
                    break;
                case PacketType.ChannelOpen:
                    p = new ChannelOpenPacket(packet);
                    length = p.Index;
                    if (ReceivedChannelOpenPacket != null)
                        return ReceivedChannelOpenPacket(p);
                    break;
                case PacketType.CreatureMove:
                    p = new CreatureMovePacket(packet);
                    length = p.Index;
                    if (ReceivedCreatureMovePacket != null)
                        return ReceivedCreatureMovePacket(p);
                    break;
                case PacketType.TileAnimation:
                    p = new TileAnimationPacket(packet);
                    length = p.Index;
                    if (ReceivedTileAnimationPacket != null)
                        return ReceivedTileAnimationPacket(p);
                    break;
                case PacketType.CreatureSquare:
                    p = new CreatureSquarePacket(packet);
                    length = p.Index;
                    if (ReceivedCreatureSquarePacket != null)
                        return ReceivedCreatureSquarePacket(p);
                    break;
                case PacketType.StatusUpdate:
                    p = new StatusUpdatePacket(packet);
                    length = p.Index;
                    if (ReceivedStatusUpdatePacket != null)
                        return ReceivedStatusUpdatePacket(p);
                    break;
                case PacketType.MapItemRemove:
                    p = new MapItemRemovePacket(packet);
                    length = p.Index;
                    if (ReceivedMapItemRemovePacket != null)
                        return ReceivedMapItemRemovePacket(p);
                    break;
                case PacketType.MapItemAdd:
                    p = new MapItemAddPacket(packet);
                    length = p.Index;
                    if (ReceivedMapItemAddPacket != null)
                        return ReceivedMapItemAddPacket(p);
                    break;
            }
            return true;
        }

        private void ProcessClientSendQueue()
        {
            if (clientSendQueue.Count > 0 && !writingToClient)
            {
                byte[] packet = clientSendQueue.Dequeue();
                writingToClient = true;
                try
                {
                    netStreamClient.BeginWrite(packet, 0, packet.Length, ClientWriteDone, null);
                }
                catch(IOException e)
                {
                    // Client crash
                    Stop();
                    if (OnCrash != null)
                        OnCrash();
                    return;
                }
            }
        }

        private void ClientWriteDone(IAsyncResult ar)
        {
            netStreamClient.EndWrite(ar);
            writingToClient = false;
            ProcessClientSendQueue();
        }
        #endregion

        #region Client -> Server
        private void ReceiveFromClient(IAsyncResult ar)
        {
            if (!netStreamClient.CanRead) return;

            int bytesRead = netStreamClient.EndRead(ar);

            // Special case, client is logging out
            if (GetPacketType(dataClient) == (byte)PacketType.Logout &&
                !client.GetPlayer().HasFlag(Tibia.Constants.Flag.Battle))
            {
                // Notify the server
                netStreamServer.BeginWrite(dataClient, 0, bytesRead, null, null);

                Stop();
                Restart();

                isLoggedIn = false;

                // Notify that the client has logged out
                if (OnLogOut != null)
                {
                    // We don't care about the return to this
                    MethodInvoker invoker = new MethodInvoker(OnLogOut);
                    invoker.BeginInvoke(null, null);
                }
                    
                return;
            }

            if (bytesRead > 0)
            {
                // Parse the data into a single packet
                byte[] packet = new byte[bytesRead];
                Array.Copy(dataClient, packet, bytesRead);

                // Enqueue the packet for processing
                clientReceiveQueue.Enqueue(packet);
            }

            ProcessClientReceiveQueue();

            try
            {
                netStreamClient.BeginRead(dataClient, 0, dataClient.Length, (AsyncCallback)ReceiveFromClient, null);
            }
            catch (IOException e)
            {
                // Client crashed
                Stop();
                if (OnCrash != null)
                    OnCrash();
                return;
            }
        }

        private void Stop()
        {
            connected = false;
            netStreamClient.Close();
            netStreamServer.Close();
            netStreamLogin.Close();
            tcpClient.Stop();
            tcpServer.Close();
            tcpLogin.Close();
            socketClient.Close();
        }

        private void ProcessClientReceiveQueue()
        {
            if (clientReceiveQueue.Count > 0)
            {
                byte[] original = clientReceiveQueue.Dequeue();
                byte[] decrypted = DecryptPacket(original);

                // Always call the default (if attached to)
                if (ReceivedPacketFromClient != null)
                    ReceivedPacketFromClient(new Packet(decrypted));


                bool forward = RaiseOutgoingEvents(decrypted);
                if (forward)
                {
                    serverSendQueue.Enqueue(original);
                    ProcessServerSendQueue();
                }

                if (clientReceiveQueue.Count > 0)
                    ProcessClientReceiveQueue();
            }
        }

        private bool RaiseOutgoingEvents(byte[] packet)
        {
            if (packet.Length < 3) return true;
            switch ((PacketType)packet[2])
            {
                case PacketType.PlayerSpeech:
                    if (ReceivedPlayerSpeechPacket != null)
                        return ReceivedPlayerSpeechPacket(new PlayerSpeechPacket(packet));
                    break;
                case PacketType.ClientLoggedIn:
                    if (!isLoggedIn)
                    {
                        isLoggedIn = true;
                        if (OnLogIn != null)
                        {
                            // Call OnLogIn on a seperate thread after a little time to make sure
                            // the client has initialized the GUI
                            MethodInvoker invoker = new MethodInvoker(BeginOnLogIn);
                            invoker.BeginInvoke(null, null);
                        }
                    }
                    break;
            }
            return true;
        }

        private void BeginOnLogIn()
        {
            Thread.Sleep(loginDelay);
            OnLogIn();
        }

        private void ProcessServerSendQueue()
        {
            if (serverSendQueue.Count > 0 && !writingToServer)
            {
                TimeSpan diff = DateTime.UtcNow - lastServerWrite;
                if (diff.TotalMilliseconds < 125)
                    Thread.Sleep((int)diff.TotalMilliseconds);
                byte[] packet = serverSendQueue.Dequeue();
                writingToServer = true;
                netStreamServer.BeginWrite(packet, 0, packet.Length, ServerWriteDone, null);
            }
        }

        private void ServerWriteDone(IAsyncResult ar)
        {
            netStreamServer.EndWrite(ar);
            lastServerWrite = DateTime.UtcNow;
            writingToServer = false;
            ProcessServerSendQueue();
        }
        #endregion

        #region Inject Packets
        /// <summary>
        /// Encrypts and sends a packet to the server
        /// </summary>
        /// <param name="packet"></param>
        public void SendToServer(byte[] packet)
        {
            byte[] encrypted = EncryptPacket(packet);
            serverSendQueue.Enqueue(encrypted);
            ProcessServerSendQueue();
        }
        /// <summary>
        /// Encrypts and sends a packet to the client
        /// </summary>
        /// <param name="packet"></param>
        public void SendToClient(byte[] packet)
        {
            //MessageBox.Show(Packet.ByteArrayToHexString(packet));
            //return;
            byte[] encrypted = EncryptPacket(packet);
            clientSendQueue.Enqueue(encrypted);
            ProcessClientSendQueue();
        }
        #endregion

        #region Encryption
        /// <summary>
        /// Wrapper for XTEA.Encrypt
        /// </summary>
        /// <param name="packet"></param>
        /// <returns></returns>
        public byte[] EncryptPacket(byte[] packet)
        {
            return XTEA.Encrypt(packet, client.ReadBytes(Addresses.Client.XTeaKey, 16));
        }

        /// <summary>
        /// Wrapper for XTEA.Decrypt
        /// </summary>
        /// <param name="packet"></param>
        /// <returns></returns>
        public byte[] DecryptPacket(byte[] packet)
        {
            return XTEA.Decrypt(packet, client.ReadBytes(Addresses.Client.XTeaKey, 16));
        }

        /// <summary>
        /// Get the type of an encrypted packet
        /// </summary>
        /// <param name="packet"></param>
        /// <returns></returns>
        private byte GetPacketType(byte[] packet)
        {
            return XTEA.DecryptType(packet, client.ReadBytes(Addresses.Client.XTeaKey, 16));
        }
        #endregion

        #region Port Checking
        /// <summary>
        /// Check if a port is open on localhost
        /// </summary>
        /// <param name="port"></param>
        /// <returns></returns>
        public static bool CheckPort(int port)
        {
            try
            {
                tcpScan = new TcpListener(IPAddress.Any, port);
                tcpScan.Start();
                tcpScan.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Get the first free port on localhost starting at the default 7171
        /// </summary>
        /// <returns></returns>
        public static short GetFreePort()
        {
            return GetFreePort(DefaultPort);
        }

        /// <summary>
        /// Get the first free port on localhost beginning at start
        /// </summary>
        /// <param name="start"></param>
        /// <returns></returns>
        public static short GetFreePort(short start)
        {
            while (!CheckPort(start))
            {
                start++;
            }
            return start;
        }
        #endregion
    }
}
