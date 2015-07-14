//#define DEBUG_OUT
//#define SEND_UPDATES_TO_SENDER

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.Net.Sockets;
using System.Threading;
using System.Net;
using System.IO;
using System.Diagnostics;

using System.Collections.Concurrent;

namespace KLFServer
{
class Server
{

    public struct ClientMessage
    {
        public int clientIndex;
        public KLFCommon.ClientMessageID id;
        public byte[] data;
    }

    public const long CLIENT_TIMEOUT_DELAY = 32000;
    public const long CLIENT_HANDSHAKE_TIMEOUT_DELAY = 22000;
    public const int SLEEP_TIME = 15;
    public const int MAX_SCREENSHOT_COUNT = 10000;
    public const int UDP_ACK_THROTTLE = 1000;
    public const int MAX_SAVED_THROTTLE_STATES = 16;
    public int SHARED_SCREEN_SHOTS = 0;
    public const float NOT_IN_FLIGHT_UPDATE_WEIGHT = 1.0f/4.0f;
    public const int ACTIVITY_RESET_DELAY = 10000;

    public const String SCREENSHOT_DIR = "C:/Inetpub/vhosts/gaterunner.com/ksp.gaterunner.com";
    public const String BAN_FILE = "banned.txt";

    public int numClients
    {
        private set;
        get;
    }

    public int numInGameClients
    {
        private set;
        get;
    }

    public int numInFlightClients
    {
        private set;
        get;
    }

    public bool quit = false;
    public bool stop = false;

    public String threadExceptionStackTrace;
    public Exception threadException;

    public object threadExceptionLock = new object();
    public object clientActivityCountLock = new object();
    public static object consoleWriteLock = new object();

    public Thread listenThread;
    public Thread commandThread;
    public Thread connectionThread;
    public Thread outgoingMessageThread;

    public TcpListener tcpListener;
    public UdpClient udpClient;

    public HttpListener httpListener;

    public ServerClient[] clients;
    public ConcurrentQueue<ClientMessage> clientMessageQueue;

    public HashSet<IPAddress> bannedIPs = new HashSet<IPAddress>();

    public Dictionary<IPAddress, ServerClient.ThrottleState> savedThrottleStates = new Dictionary<IPAddress, ServerClient.ThrottleState>();

    public ServerSettings settings;

    public Stopwatch stopwatch = new Stopwatch();

    public long currentMillisecond
    {
        get
        {
            return stopwatch.ElapsedMilliseconds;
        }
    }

    public int updateInterval
    {
        get
        {
            float relevant_player_count = 0;

            lock (clientActivityCountLock)
            {
                //Create a weighted count of clients in-flight and not in-flight to estimate the amount of update traffic
                relevant_player_count = numInFlightClients + (numInGameClients - numInFlightClients) * NOT_IN_FLIGHT_UPDATE_WEIGHT;
            }

            if (relevant_player_count <= 0)
                return ServerSettings.MIN_UPDATE_INTERVAL;

            //Calculate the value that satisfies updates per second
            int val = (int)Math.Round(1.0f / (settings.updatesPerSecond / relevant_player_count) * 1000);

            //Bound the values by the minimum and maximum
            if (val < ServerSettings.MIN_UPDATE_INTERVAL)
                return ServerSettings.MIN_UPDATE_INTERVAL;

            if (val > ServerSettings.MAX_UPDATE_INTERVAL)
                return ServerSettings.MAX_UPDATE_INTERVAL;

            return val;
        }
    }

    public byte inactiveShipsPerClient
    {
        get
        {
            int relevant_player_count = 0;

            lock (clientActivityCountLock)
            {
                relevant_player_count = numInFlightClients;
            }

            if (relevant_player_count <= 0)
                return settings.totalInactiveShips;

            if (relevant_player_count > settings.totalInactiveShips)
                return 0;

            return (byte)(settings.totalInactiveShips / relevant_player_count);

        }
    }

    //Methods

    public Server(ServerSettings settings)
    {
        this.settings = settings;
    }

    public static void stampedConsoleWriteLine(String message)
    {
        lock (consoleWriteLock)
        {

            ConsoleColor default_color = Console.ForegroundColor;

            try
            {
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.Write('[');
                Console.Write(DateTime.Now.ToString("HH:mm:ss"));
                Console.Write("] ");

                Console.ForegroundColor = default_color;
                Console.WriteLine(message);
            }
            catch (IOException) { }
            finally
            {
                Console.ForegroundColor = default_color;
            }

        }
    }

    public static void debugConsoleWriteLine(String message)
    {
#if DEBUG_OUT
        stampedConsoleWriteLine(message);
#endif
    }

    public void clearState()
    {
        safeAbort(listenThread);
        safeAbort(commandThread);
        safeAbort(connectionThread);
        safeAbort(outgoingMessageThread);

        if (clients != null)
        {
            for (int i = 0; i < clients.Length; i++)
            {
                clients[i].endReceivingMessages();
                if (clients[i].tcpClient != null)
                    clients[i].tcpClient.Close();
            }
        }

        if (tcpListener != null)
        {
            try
            {
                tcpListener.Stop();
            }
            catch (System.Net.Sockets.SocketException)
            {
            }
        }

        if (httpListener != null)
        {
            try
            {
                httpListener.Stop();
                httpListener.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (udpClient != null)
        {
            try
            {
                udpClient.Close();
            }
            catch { }
        }

        udpClient = null;

    }

    public void saveScreenshot(Screenshot screenshot, String player)
    {
        if (!Directory.Exists(SCREENSHOT_DIR))
        {
            //Create the screenshot directory
            try
            {
                if (!Directory.CreateDirectory(SCREENSHOT_DIR).Exists)
                    return;
            }
            catch (Exception)
            {
                return;
            }
        }
        string PlayerDir = SCREENSHOT_DIR;
        PlayerDir += "/" + KLFCommon.filteredFileName(player);
        if (!Directory.Exists(PlayerDir))
        {
            //Create the screenshot directory
            try
            {
                if (!Directory.CreateDirectory(PlayerDir).Exists)
                    return;
            }
            catch (Exception)
            {
                return;
            }
        }
        //Build the filename
        StringBuilder sb = new StringBuilder();
        sb.Append(PlayerDir);
        sb.Append('/');
        sb.Append(KLFCommon.filteredFileName(player));
        sb.Append(' ');
        sb.Append(System.DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss"));
        sb.Append(".png");

        //Write the screenshot to file
        String filename = sb.ToString();
        if (!File.Exists(filename))
        {
            try
            {
                File.WriteAllBytes(filename, screenshot.image);
            }
            catch (Exception)
            {
            }
        }
    }

    private void safeAbort(Thread thread, bool join = false)
    {
        if (thread != null)
        {
            try
            {
                thread.Abort();
                if (join)
                    thread.Join();
            }
            catch (ThreadStateException) { }
            catch (ThreadInterruptedException) { }
        }
    }

    public void passExceptionToMain(Exception e)
    {
        lock (threadExceptionLock)
        {
            if (threadException == null)
                threadException = e; //Pass exception to main thread
        }
    }

    private void printCommands()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("/quit - close server");
        Console.WriteLine("/stop - stop hosting server");
        Console.WriteLine("/list - list players");
        Console.WriteLine("/count - display player counts");
        Console.WriteLine("/kick <username> - kick a player");
        Console.WriteLine("/ban <username> - ban a player");
        Console.WriteLine("/banip <ip> - ban an ip");
        Console.WriteLine("/unbanip <ip> - unban an ip");
        Console.WriteLine("/clearbans - remove all bans");
    }

    //Threads

    public void hostingLoop()
    {
        clearState();

        //Start hosting server
        stopwatch.Start();

        stampedConsoleWriteLine("Hosting server on port " + settings.port + "...");

        clients = new ServerClient[settings.maxClients];
        for (int i = 0; i < clients.Length; i++)
        {
            clients[i] = new ServerClient(this, i);
        }

        clientMessageQueue = new ConcurrentQueue<ClientMessage>();

        numClients = 0;
        numInGameClients = 0;
        numInFlightClients = 0;

        listenThread = new Thread(new ThreadStart(listenForClients));
        commandThread = new Thread(new ThreadStart(handleCommands));
        connectionThread = new Thread(new ThreadStart(handleConnections));
        outgoingMessageThread = new Thread(new ThreadStart(sendOutgoingMessages));

        threadException = null;

        loadBanList();

        tcpListener = new TcpListener(IPAddress.Any, settings.port);
        listenThread.Start();

        try
        {
            udpClient = new UdpClient(settings.port);
            udpClient.BeginReceive(asyncUDPReceive, null);
        }
        catch
        {
            udpClient = null;
        }

        Console.WriteLine("Enter /help to view server commands.");

        commandThread.Start();
        connectionThread.Start();
        outgoingMessageThread.Start();

        //Begin listening for HTTP requests

        httpListener = new HttpListener(); //Might need a replacement as HttpListener needs admin rights
        try
        {
            httpListener.Prefixes.Add("http://*:" + settings.httpPort + '/');
            httpListener.Start();
            httpListener.BeginGetContext(asyncHTTPCallback, httpListener);
        }
        catch (Exception e)
        {
            stampedConsoleWriteLine("Error starting http server: " + e);
            stampedConsoleWriteLine("Please try running the server as an administrator");
        }

        while (!quit)
        {
            //Check for exceptions that occur in threads
            lock (threadExceptionLock)
            {
                if (threadException != null)
                {
                    Exception e = threadException;
                    threadExceptionStackTrace = e.StackTrace;
                    throw e;
                }
            }

            Thread.Sleep(SLEEP_TIME);
        }

        clearState();
        stopwatch.Stop();

        stampedConsoleWriteLine("Server session ended.");

    }

    private void handleCommands()
    {
        try
        {
            while (true)
            {
                String input = Console.ReadLine().ToLower();

                if (input != null && input.Length > 0)
                {

                    if (input.ElementAt(0) == '/')
                    {
                        if (input == "/quit" || input == "/stop")
                        {
                            quit = true;
                            if (input == "/stop")
                                stop = true;

                            //Disconnect all clients
                            for (int i = 0; i < clients.Length; i++)
                                disconnectClient(i, "Server is shutting down");

                            break;
                        }
                        else if (input == "/crash1111")
                        {
                            //Object o = null; //You asked for it!
                            //o.ToString();
                        }
                        else if (input.Length > 6 && input.Substring(0, 6) == "/kick ")
                        {
                            String name = input.Substring(6, input.Length - 6).ToLower();
                            int index = getClientIndexByName(name);
                            if (index >= 0)
                                disconnectClient(index, "You were kicked from the server.");
                            else
                                stampedConsoleWriteLine("Player " + name + " not found.");
                        }
                        else if (input.Length > 5 && input.Substring(0, 5) == "/ban ")
                        {
                            String name = input.Substring(5, input.Length - 5).ToLower();
                            int index = getClientIndexByName(name);
                            if (index >= 0)
                                banClient(index);
                            else
                                stampedConsoleWriteLine("Player " + name + " not found.");
                        }
                        else if (input.Length > 7 && input.Substring(0, 7) == "/banip ")
                        {
                            String ip_str = input.Substring(7, input.Length - 7).ToLower();
                            IPAddress address;
                            if (IPAddress.TryParse(ip_str, out address))
                                banIP(address);
                            else
                                stampedConsoleWriteLine("Invalid ip.");
                        }
                        else if (input.Length > 9 && input.Substring(0, 9) == "/unbanip ")
                        {
                            String ip_str = input.Substring(9, input.Length - 9).ToLower();
                            IPAddress address;
                            if (IPAddress.TryParse(ip_str, out address))
                                unbanIP(address);
                            else
                                stampedConsoleWriteLine("Invalid ip.");
                        }
                        else if (input == "/clearbans")
                        {
                            clearBans();
                            stampedConsoleWriteLine("All bans cleared.");
                        }
                        else if (input.Length > 4 && input.Substring(0, 4) == "/ip ")
                        {
                            String name = input.Substring(4, input.Length - 4).ToLower();
                            int index = getClientIndexByName(name);
                            if (index >= 0)
                            {
                                stampedConsoleWriteLine(clients[index].username + " ip: " + getClientIP(index).ToString());
                            }
                        }
                        else if (input == "/list")
                        {
                            //Display player list
                            StringBuilder sb = new StringBuilder();
                            for (int i = 0; i < clients.Length; i++)
                            {
                                if (clientIsReady(i))
                                {
                                    sb.Append(clients[i].username);
                                    sb.Append(" - ");
                                    sb.Append(clients[i].activityLevel.ToString());
                                    sb.Append('\n');
                                }
                            }

                            stampedConsoleWriteLine(sb.ToString());
                        }
                        else if (input == "/count")
                        {
                            stampedConsoleWriteLine("Total clients: " + numClients);

                            lock (clientActivityCountLock)
                            {
                                stampedConsoleWriteLine("In-Game Clients: " + numInGameClients);
                                stampedConsoleWriteLine("In-Flight Clients: " + numInFlightClients);
                            }
                        }
                        else if (input == "/help")
                            printCommands();
                    }
                    else
                    {
                        //Send a message to all clients
                        sendServerMessageToAll(input);
                    }

                }
            }
        }
        catch (ThreadAbortException)
        {
        }
        catch (Exception e)
        {
            passExceptionToMain(e);
        }
    }

    private void listenForClients()
    {

        try
        {
            stampedConsoleWriteLine("Listening for clients...");
            tcpListener.Start(4);

            while (true)
            {

                TcpClient client = null;
                String error_message = String.Empty;

                try
                {
                    if (tcpListener.Pending())
                    {
                        client = tcpListener.AcceptTcpClient(); //Accept a TCP client
                    }
                }
                catch (System.Net.Sockets.SocketException e)
                {
                    if (client != null)
                        client.Close();
                    client = null;
                    error_message = e.ToString();
                }

                if (client != null && client.Connected)
                {
                    IPAddress client_address = ((IPEndPoint)client.Client.RemoteEndPoint).Address;

                    //Check if the client IP has been banned
                    if (bannedIPs.Contains(client_address))
                    {
                        //Client has been banned
                        stampedConsoleWriteLine("Banned client: " + client_address.ToString() + " attempted to connect.");
                        sendHandshakeRefusalMessageDirect(client, "You are banned from the server.");
                        client.Close();
                    }
                    else
                    {
                        //Try to add the client
                        int client_index = addClient(client);
                        if (client_index >= 0)
                        {
                            if (clientIsValid(client_index))
                            {
                                //Send a handshake to the client
                                stampedConsoleWriteLine("Accepted client. Handshaking...");
                                sendHandshakeMessage(client_index);

                                try
                                {
                                    sendMessageHeaderDirect(client, KLFCommon.ServerMessageID.NULL, 0);
                                }
                                catch (System.IO.IOException)
                                {
                                }
                                catch (System.ObjectDisposedException)
                                {
                                }
                                catch (System.InvalidOperationException)
                                {
                                }

                                //Send the join message to the client
                                if (settings.joinMessage.Length > 0)
                                    sendServerMessage(client_index, settings.joinMessage);
                            }

                            //Send a server setting update to all clients
                            sendServerSettingsToAll();
                        }
                        else
                        {
                            //Client array is full
                            stampedConsoleWriteLine("Client attempted to connect, but server is full.");
                            sendHandshakeRefusalMessageDirect(client, "Server is currently full");
                            client.Close();
                        }
                    }
                }
                else
                {
                    if (client != null)
                        client.Close();
                    client = null;
                }

                if (client == null && error_message.Length > 0)
                {
                    //There was an error accepting the client
                    stampedConsoleWriteLine("Error accepting client: ");
                    stampedConsoleWriteLine(error_message);
                }

                Thread.Sleep(SLEEP_TIME);

            }
        }
        catch (ThreadAbortException)
        {
        }
        catch (Exception e)
        {
            passExceptionToMain(e);
        }
    }

    private void handleConnections()
    {
        try
        {
            debugConsoleWriteLine("Starting disconnect thread");

            while (true)
            {
                //Handle received messages
                while (clientMessageQueue.Count > 0)
                {
                    ClientMessage message;

                    if (clientMessageQueue.TryDequeue(out message))
                        handleMessage(message.clientIndex, message.id, message.data);
                    else
                        break;
                }


                //Check for clients that have not sent messages for too long
                for (int i = 0; i < clients.Length; i++)
                {
                    if (clientIsValid(i))
                    {
                        long last_receive_time = 0;
                        long connection_start_time = 0;
                        bool handshook = false;

                        lock (clients[i].timestampLock)
                        {
                            last_receive_time = clients[i].lastReceiveTime;
                            connection_start_time = clients[i].connectionStartTime;
                            handshook = clients[i].receivedHandshake;
                        }

                        if (currentMillisecond - last_receive_time > CLIENT_TIMEOUT_DELAY
                                || (!handshook && (currentMillisecond - connection_start_time) > CLIENT_HANDSHAKE_TIMEOUT_DELAY))
                        {
                            //Disconnect the client
                            disconnectClient(i, "Timeout");
                        }
                        else
                        {
                            bool changed = false;

                            //Reset the client's activity level if the time since last update was too long
                            lock (clients[i].activityLevelLock)
                            {
                                if (clients[i].activityLevel == ServerClient.ActivityLevel.IN_FLIGHT
                                        && (currentMillisecond - clients[i].lastInFlightActivityTime) > ACTIVITY_RESET_DELAY)
                                {
                                    clients[i].activityLevel = ServerClient.ActivityLevel.IN_GAME;
                                    changed = true;
                                }

                                if (clients[i].activityLevel == ServerClient.ActivityLevel.IN_GAME
                                        && (currentMillisecond - clients[i].lastInGameActivityTime) > ACTIVITY_RESET_DELAY)
                                {
                                    clients[i].activityLevel = ServerClient.ActivityLevel.INACTIVE;
                                    changed = true;
                                }
                            }

                            if (changed)
                                clientActivityLevelChanged(i);

                        }
                    }
                    else if (!clients[i].canBeReplaced)
                    {
                        //Client is disconnected but slot has not been cleaned up
                        disconnectClient(i, "Connection lost");
                    }

                }

                Thread.Sleep(SLEEP_TIME);
            }
        }
        catch (ThreadAbortException)
        {
        }
        catch (Exception e)
        {
            passExceptionToMain(e);
        }

        debugConsoleWriteLine("Ending disconnect thread.");
    }

    void sendOutgoingMessages()
    {
        try
        {

            while (true)
            {
                for (int i = 0; i < clients.Length; i++)
                {
                    if (clientIsValid(i))
                        clients[i].sendOutgoingMessages();
                }

                Thread.Sleep(SLEEP_TIME);
            }

        }
        catch (ThreadAbortException)
        {
        }
        catch (Exception e)
        {
            passExceptionToMain(e);
        }
    }

    //Clients

    private int addClient(TcpClient tcp_client)
    {

        if (tcp_client == null || !tcp_client.Connected)
            return -1;

        //Find an open client slot
        for (int i = 0; i < clients.Length; i++)
        {
            ServerClient client = clients[i];

            //Check if the client is valid
            if (client.canBeReplaced && !clientIsValid(i))
            {

                //Add the client
                client.tcpClient = tcp_client;
                client.ip = ((IPEndPoint)tcp_client.Client.RemoteEndPoint).Address;

                //Reset client properties
                client.resetProperties();

                //If the client's throttle state has been saved, retrieve it
                if (savedThrottleStates.TryGetValue(client.ip, out client.throttleState))
                    savedThrottleStates.Remove(client.ip);

                client.startReceivingMessages();
                numClients++;

                return i;
            }

        }

        return -1;
    }

    public bool clientIsValid(int index)
    {
        return index >= 0 && index < clients.Length && clients[index].tcpClient != null && clients[index].tcpClient.Connected;
    }

    public bool clientIsReady(int index)
    {
        return clientIsValid(index) && clients[index].receivedHandshake;
    }

    public void disconnectClient(int index, String message)
    {
        //Send a message to client informing them why they were disconnected
        if (clients[index].tcpClient.Connected)
            sendConnectionEndMessageDirect(clients[index].tcpClient, message);

        //Close the socket
        lock (clients[index].tcpClientLock)
        {

            clients[index].endReceivingMessages();
            clients[index].tcpClient.Close();

            if (clients[index].canBeReplaced)
                return;

            numClients--;

            //Only send the disconnect message if the client performed handshake successfully
            if (clients[index].receivedHandshake)
            {
                stampedConsoleWriteLine("Client #" + index + " " + clients[index].username + " has disconnected: " + message);

                if (!clients[index].messagesThrottled)
                {

                    StringBuilder sb = new StringBuilder();

                    //Build disconnect message
                    sb.Clear();
                    sb.Append("User ");
                    sb.Append(clients[index].username);
                    sb.Append(" has disconnected : " + message);

                    //Send the disconnect message to all other clients
                    sendServerMessageToAll(sb.ToString());
                }

                messageFloodIncrement(index);
            }
            else
                stampedConsoleWriteLine("Client failed to handshake successfully: " + message);

            clients[index].receivedHandshake = false;

            if (clients[index].activityLevel != ServerClient.ActivityLevel.INACTIVE)
                clientActivityLevelChanged(index);
            else
                sendServerSettingsToAll();

            //Save the client's throttle state
            IPAddress ip = getClientIP(index);
            if (savedThrottleStates.ContainsKey(ip))
            {
                savedThrottleStates[ip] = clients[index].throttleState;
            }
            else
            {
                if (savedThrottleStates.Count >= MAX_SAVED_THROTTLE_STATES)
                    savedThrottleStates.Clear();

                savedThrottleStates.Add(ip, clients[index].throttleState);
            }

            clients[index].disconnected();

        }
    }

    public void clientActivityLevelChanged(int index)
    {
        debugConsoleWriteLine(clients[index].username + " activity level is now " + clients[index].activityLevel);

        //Count the number of in-game/in-flight clients
        int num_in_game = 0;
        int num_in_flight = 0;

        for (int i = 0; i < clients.Length; i++)
        {
            if (clientIsValid(i))
            {
                switch (clients[i].activityLevel)
                {
                case ServerClient.ActivityLevel.IN_GAME:
                    num_in_game++;
                    break;

                case ServerClient.ActivityLevel.IN_FLIGHT:
                    num_in_game++;
                    num_in_flight++;
                    break;
                }
            }
        }

        lock (clientActivityCountLock)
        {
            numInGameClients = num_in_game;
            numInFlightClients = num_in_flight;
        }

        sendServerSettingsToAll();
    }

    private void asyncUDPReceive(IAsyncResult result)
    {
        try
        {

            IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, settings.port);
            byte[] received = udpClient.EndReceive(result, ref endpoint);

            if (received.Length >= KLFCommon.MSG_HEADER_LENGTH + 4)
            {
                int index = 0;

                //Get the sender index
                int sender_index = KLFCommon.intFromBytes(received, index);
                index += 4;

                //Get the message header data
                KLFCommon.ClientMessageID id = (KLFCommon.ClientMessageID)KLFCommon.intFromBytes(received, index);
                index += 4;

                int data_length = KLFCommon.intFromBytes(received, index);
                index += 4;

                //Get the data
                byte[] data = null;

                if (data_length > 0 && data_length <= received.Length - index)
                {
                    data = new byte[data_length];
                    Array.Copy(received, index, data, 0, data.Length);
                }

                if (clientIsReady(sender_index))
                {
                    if ((currentMillisecond - clients[sender_index].lastUDPACKTime) > UDP_ACK_THROTTLE)
                    {
                        //Acknowledge the client's message with a TCP message
                        clients[sender_index].queueOutgoingMessage(KLFCommon.ServerMessageID.UDP_ACKNOWLEDGE, null);
                        clients[sender_index].lastUDPACKTime = currentMillisecond;
                    }

                    //Handle the message
                    handleMessage(sender_index, id, data);
                }

            }

            udpClient.BeginReceive(asyncUDPReceive, null); //Begin receiving the next message

        }
        catch (ThreadAbortException)
        {
        }
        catch (Exception e)
        {
            passExceptionToMain(e);
        }
    }

    private int getClientIndexByName(String name)
    {
        name = name.ToLower(); //Set name to lowercase to make the search case-insensitive

        for (int i = 0; i < clients.Length; i++)
        {
            if (clientIsReady(i) && clients[i].username.ToLower() == name)
                return i;
        }

        return -1;
    }

    private IPAddress getClientIP(int index)
    {
        return clients[index].ip;
    }

    //Bans

    private void banClient(int index)
    {
        if (clientIsReady(index))
        {
            banIP(getClientIP(index));
            saveBanList();
            disconnectClient(index, "Banned from the server.");
        }
    }

    private void banIP(IPAddress address)
    {
        if (bannedIPs.Add(address))
        {
            stampedConsoleWriteLine("Banned ip: " + address.ToString());
            saveBanList();
        }
        else
            stampedConsoleWriteLine("IP " + address.ToString() + " was already banned.");
    }

    private void unbanIP(IPAddress address)
    {
        if (bannedIPs.Remove(address))
        {
            stampedConsoleWriteLine("Unbanned ip: " + address.ToString());
            saveBanList();
        }
        else
            stampedConsoleWriteLine("IP " + address.ToString() + " not found in ban list.");
    }

    private void clearBans()
    {
        bannedIPs.Clear();
        saveBanList();
    }

    private void loadBanList()
    {
        TextReader reader = null;
        try
        {
            bannedIPs.Clear();

            reader = File.OpenText(BAN_FILE);

            String line;

            do
            {
                line = reader.ReadLine();
                if (line != null)
                {
                    IPAddress address;
                    if (IPAddress.TryParse(line, out address))
                        bannedIPs.Add(address);
                }

            } while (line != null);

        }
        catch { }
        finally
        {
            if (reader != null)
                reader.Close();
        }
    }

    private void saveBanList()
    {
        try
        {
            if (File.Exists(BAN_FILE))
                File.Delete(BAN_FILE);

            TextWriter writer = File.CreateText(BAN_FILE);

            foreach (IPAddress address in bannedIPs)
                writer.WriteLine(address.ToString());

            writer.Close();
        }
        catch {}
    }

    //HTTP

    private void asyncHTTPCallback(IAsyncResult result)
    {
        try
        {
            HttpListener listener = (HttpListener)result.AsyncState;

            HttpListenerContext context = listener.EndGetContext(result);
            HttpListenerRequest request = context.Request;

            HttpListenerResponse response = context.Response;

            //Build response string
            StringBuilder response_builder = new StringBuilder();
            response_builder.Append("<!DOCTYPE HTML PUBLIC \"-//W3C//DTD HTML 4.01 Transitional//EN\"><html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=ISO-8859-1\"><title>Kerbal Live Feed</title>");
            response_builder.Append("<meta name=\"generator\" content=\"WYSIWYG Web Builder - http://www.wysiwygwebbuilder.com\"></head><body bgcolor=\"#FFFFFF\" text=\"#000000\">");
            response_builder.Append("<div id=\"wb_Text1\" style=\"position:absolute;left:22px;top:10px;width:764px;height:94px;z-index:0\" align=\"center\">");
            response_builder.Append("<font style=\"font-size:32px\" color=\"#000000\" face=\"Stencil\">KERBAL LIVE FEED SERVER<br>");
            response_builder.Append(settings.serverInfo);
            response_builder.Append("</font></div><div id=\"wb_Table1\" style=\"position:absolute;left:400px;top:145px;width:400px;height:400px;z-index:1\" align=\"left\">");
            response_builder.Append("<table width=\"100%\" border=\"1\" cellpadding=\"0\" cellspacing=\"1\" id=\"Table1\"><tr>");
            response_builder.Append("<td align=\"center\" valign=\"top\" width=\"396\" height=\"396\"><font style=\"font-size:19px\" color=\"#000000\" face=\"Stencil\"><u>players online:");
            response_builder.Append(numClients);
            response_builder.Append("&nbsp; MAX: ");
            response_builder.Append(settings.maxClients);
            response_builder.Append("__</u><br></font><font style=\"font-size:19px\" color=\"#000000\" face=\"Arial\">");
                       bool first = true;
            for (int i = 0; i < clients.Length; i++)
            {
                if (clientIsReady(i))
                {
                    if (first)
                        first = false;
                    else
                        response_builder.Append("<br>");

                    response_builder.Append(clients[i].username);
                }
            }
            response_builder.Append("</font></td></tr></table></div><div id=\"wb_Text2\" style=\"position:absolute;left:9px;top:145px;width:310px;height:192px;z-index:2\" align=\"left\"><font style=\"font-size:11px\" color=\"#000000\" face=\"Arial\">Version:");
            response_builder.Append(KLFCommon.PROGRAM_VERSION);
            response_builder.Append("<br>");
             response_builder.Append("Port: ");
            response_builder.Append(settings.port);
            response_builder.Append("<br><br>");
            response_builder.Append("Updates per Second: ");
            response_builder.Append(settings.updatesPerSecond);
            response_builder.Append("<br>");

            response_builder.Append("Inactive Ship Limit: ");
            response_builder.Append(settings.totalInactiveShips);
            response_builder.Append("<br><br>");

            response_builder.Append("Screenshot Height: ");
            response_builder.Append(settings.screenshotSettings.maxHeight);
            response_builder.Append("<br>");

            response_builder.Append("Screenshot Save: ");
            response_builder.Append(settings.saveScreenshots);
            response_builder.Append("<br>");

            response_builder.Append("Screenshot Backlog: ");
            response_builder.Append(settings.screenshotBacklog);
            response_builder.Append("<br><br>");

            response_builder.Append("UPTIME:");
            var runtime = DateTime.Now - Process.GetCurrentProcess().StartTime;
            response_builder.Append(runtime);
            response_builder.Append("<br><br>Memory Usage:");
            long workingSet = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
            workingSet /= 2602325;
            response_builder.Append(workingSet);
            response_builder.Append(":MB<br><br>Shared Screenshots: ");
            response_builder.Append(SHARED_SCREEN_SHOTS);
            response_builder.Append("</font></div>");
            response_builder.Append("<div id=\"wb_Text3\" style=\"position:absolute;left:0px;top:500px;width:400px;height:32px;z-index:3\" align=\"center\"><font style=\"font-size:11px\" color=\"#000000\" face=\"Arial\">SCREENSHOT GALLERY<br>");
            response_builder.Append("<a href=\"http://ksp.gaterunner.com\">ksp.gaterunner.com</a></font></div></body></html>");



            //Send response
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(response_builder.ToString());
            response.ContentLength64 = buffer.LongLength;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();

            //Begin listening for the next http request
            listener.BeginGetContext(asyncHTTPCallback, listener);

        }
        catch (ThreadAbortException)
        {
        }
        catch (Exception e)
        {
            passExceptionToMain(e);
        }
    }

    //Messages

    public void queueClientMessage(int client_index, KLFCommon.ClientMessageID id, byte[] data)
    {
        ClientMessage message = new ClientMessage();
        message.clientIndex = client_index;
        message.id = id;
        message.data = data;

        clientMessageQueue.Enqueue(message);
    }

    public void handleMessage(int client_index, KLFCommon.ClientMessageID id, byte[] data)
    {
        if (!clientIsValid(client_index))
            return;

        debugConsoleWriteLine("Message id: " + id.ToString() + " data: " + (data != null ? data.Length.ToString() : "0"));

        UnicodeEncoding encoder = new UnicodeEncoding();

        switch (id)
        {
        case KLFCommon.ClientMessageID.HANDSHAKE:

            if (data != null)
            {
                StringBuilder sb = new StringBuilder();

                //Read username
                Int32 username_length = KLFCommon.intFromBytes(data, 0);
                String username = encoder.GetString(data, 4, username_length);

                int offset = 4 + username_length;

                String version = encoder.GetString(data, offset, data.Length - offset);

                String username_lower = username.ToLower();

                bool accepted = true;

                //Ensure no other players have the same username
                for (int i = 0; i < clients.Length; i++)
                {
                    if (i != client_index && clientIsReady(i) && clients[i].username.ToLower() == username_lower)
                    {
                        //Disconnect the player
                        disconnectClient(client_index, "Your username is already in use.");
                        stampedConsoleWriteLine("Rejected client due to duplicate username: " + username);
                        accepted = false;
                        break;
                    }
                }
                string[] CLVer = version.Split('.');
                string[] SVer = KLFCommon.PROGRAM_VERSION.Split('.');
                if (CLVer[1] != SVer[1] )
                {
                        //Disconnect the player BACKHERE
                        disconnectClient(client_index, "Your Client is Outdated Please Update");
                        stampedConsoleWriteLine("Rejected client due to wrong version : " + version + " New "+ KLFCommon.PROGRAM_VERSION);
                        accepted = false;
                        break;
                }
                if (!accepted)
                    break;

                //Send the active user count to the client
                if (numClients == 2)
                {
                    //Get the username of the other user on the server
                    sb.Append("There is currently 1 other user on this server: ");
                    for (int i = 0; i < clients.Length; i++)
                    {
                        if (i != client_index && clientIsReady(i))
                        {
                            sb.Append(clients[i].username);
                            break;
                        }
                    }
                }
                else
                {
                    sb.Append("There are currently ");
                    sb.Append(numClients - 1);
                    sb.Append(" other users on this server.");
                    if (numClients > 1)
                    {
                        sb.Append(" Enter !list to see them.");
                    }
                }

                clients[client_index].username = username;
                clients[client_index].receivedHandshake = true;

                sendServerMessage(client_index, sb.ToString());
                sendServerSettings(client_index);

                stampedConsoleWriteLine(username + " ("+getClientIP(client_index).ToString()+") has joined the server using client version " + version);

                if (!clients[client_index].messagesThrottled)
                {

                    //Build join message
                    sb.Clear();
                    sb.Append("User ");
                    sb.Append(username);
                    sb.Append(" has joined the server.");

                    //Send the join message to all other clients
                    sendServerMessageToAll(sb.ToString(), client_index);
                }

                messageFloodIncrement(client_index);

            }

            break;

        case KLFCommon.ClientMessageID.PRIMARY_PLUGIN_UPDATE:
        case KLFCommon.ClientMessageID.SECONDARY_PLUGIN_UPDATE:

            if (data != null && clientIsReady(client_index))
            {
#if SEND_UPDATES_TO_SENDER
                sendPluginUpdateToAll(data, id == KLFCommon.ClientMessageID.SECONDARY_PLUGIN_UPDATE);
#else
                sendPluginUpdateToAll(data, id == KLFCommon.ClientMessageID.SECONDARY_PLUGIN_UPDATE, client_index);
#endif
            }

            break;

        case KLFCommon.ClientMessageID.TEXT_MESSAGE:

            if (data != null && clientIsReady(client_index))
                handleClientTextMessage(client_index, encoder.GetString(data, 0, data.Length));

            break;

        case KLFCommon.ClientMessageID.SCREEN_WATCH_PLAYER:

            if (!clientIsReady(client_index) || data == null || data.Length < 9)
                break;

            bool send_screenshot = data[0] != 0;
            int watch_index = KLFCommon.intFromBytes(data, 1);
            int current_index = KLFCommon.intFromBytes(data, 5);
            String watch_name = encoder.GetString(data, 9, data.Length - 9);

            bool watch_name_changed = false;

            lock (clients[client_index].watchPlayerNameLock)
            {
                if (watch_name != clients[client_index].watchPlayerName || watch_index != clients[client_index].watchPlayerIndex)
                {
                    //Set the watch player name
                    clients[client_index].watchPlayerIndex = watch_index;
                    clients[client_index].watchPlayerName = watch_name;
                    watch_name_changed = true;
                }
            }

            if (send_screenshot && watch_name_changed && watch_name.Length > 0)
            {
                //Try to find the player the client is watching and send that player's current screenshot
                int watched_index = getClientIndexByName(watch_name);
                if (clientIsReady(watched_index))
                {
                    Screenshot screenshot = null;
                    lock (clients[watched_index].screenshotLock)
                    {
                        screenshot = clients[watched_index].getScreenshot(watch_index);
                        if (screenshot == null && watch_index == -1)
                            screenshot = clients[watched_index].lastScreenshot;
                    }

                    if (screenshot != null && screenshot.index != current_index)
                    {
                        sendScreenshot(client_index, screenshot);
                    }
                }
            }


            break;

        case KLFCommon.ClientMessageID.SCREENSHOT_SHARE:

            if (data != null && data.Length <= settings.screenshotSettings.maxNumBytes && clientIsReady(client_index))
            {
                if (!clients[client_index].screenshotsThrottled)
                {
                    StringBuilder sb = new StringBuilder();

                    Screenshot screenshot = new Screenshot();
                    screenshot.setFromByteArray(data);

                    //Set the screenshot for the player
                    lock (clients[client_index].screenshotLock)
                    {
                        clients[client_index].pushScreenshot(screenshot);
                    }

                    sb.Append(clients[client_index].username);
                    sb.Append(" has shared a screenshot.");

                    sendTextMessageToAll(sb.ToString());
                    stampedConsoleWriteLine(sb.ToString());

                    //Send the screenshot to every client watching the player
                    sendScreenshotToWatchers(client_index, screenshot);

                    if (settings.saveScreenshots)
                        saveScreenshot(screenshot, clients[client_index].username);
                    SHARED_SCREEN_SHOTS += 1;
                }

                bool was_throttled = clients[client_index].screenshotsThrottled;

                clients[client_index].screenshotFloodIncrement();

                if (!was_throttled && clients[client_index].screenshotsThrottled)
                {
                    long throttle_secs = settings.screenshotFloodThrottleTime / 1000;
                    sendServerMessage(client_index, "You have been restricted from sharing screenshots for " + throttle_secs + " seconds.");
                    stampedConsoleWriteLine(clients[client_index].username + " has been restricted from sharing screenshots for " + throttle_secs + " seconds.");
                }
                else if (clients[client_index].throttleState.messageFloodCounter == settings.screenshotFloodLimit - 1)
                    sendServerMessage(client_index, "Warning: You are sharing too many screenshots.");

            }

            break;

        case KLFCommon.ClientMessageID.CONNECTION_END:

            String message = String.Empty;
            if (data != null)
                message = encoder.GetString(data, 0, data.Length); //Decode the message

            disconnectClient(client_index, message); //Disconnect the client
            break;

        case KLFCommon.ClientMessageID.SHARE_CRAFT_FILE:

            if (clientIsReady(client_index) && data != null
                    && data.Length > 5 && (data.Length - 5) <= KLFCommon.MAX_CRAFT_FILE_BYTES)
            {
                if (clients[client_index].messagesThrottled)
                {
                    messageFloodIncrement(client_index);
                    break;
                }

                messageFloodIncrement(client_index);

                //Read craft name length
                byte craft_type = data[0];
                int craft_name_length = KLFCommon.intFromBytes(data, 1);
                if (craft_name_length < data.Length - 5)
                {
                    //Read craft name
                    String craft_name = encoder.GetString(data, 5, craft_name_length);

                    //Read craft bytes
                    byte[] craft_bytes = new byte[data.Length - craft_name_length - 5];
                    Array.Copy(data, 5 + craft_name_length, craft_bytes, 0, craft_bytes.Length);

                    lock (clients[client_index].sharedCraftLock)
                    {
                        clients[client_index].sharedCraftName = craft_name;
                        clients[client_index].sharedCraftFile = craft_bytes;
                        clients[client_index].sharedCraftType = craft_type;
                    }

                    //Send a message to players informing them that a craft has been shared
                    StringBuilder sb = new StringBuilder();
                    sb.Append(clients[client_index].username);
                    sb.Append(" shared ");
                    sb.Append(craft_name);

                    switch (craft_type)
                    {
                    case KLFCommon.CRAFT_TYPE_VAB:
                        sb.Append(" (VAB)");
                        break;

                    case KLFCommon.CRAFT_TYPE_SPH:
                        sb.Append(" (SPH)");
                        break;
                    }

                    stampedConsoleWriteLine(sb.ToString());

                    sb.Append(" . Enter !getcraft ");
                    sb.Append(clients[client_index].username);
                    sb.Append(" to get it.");
                    sendTextMessageToAll(sb.ToString());
                }
            }
            break;

        case KLFCommon.ClientMessageID.ACTIVITY_UPDATE_IN_FLIGHT:
            clients[client_index].updateActivityLevel(ServerClient.ActivityLevel.IN_FLIGHT);
            break;

        case KLFCommon.ClientMessageID.ACTIVITY_UPDATE_IN_GAME:
            clients[client_index].updateActivityLevel(ServerClient.ActivityLevel.IN_GAME);
            break;

        case KLFCommon.ClientMessageID.PING:
            clients[client_index].queueOutgoingMessage(KLFCommon.ServerMessageID.PING_REPLY, null);
            break;

        }

        debugConsoleWriteLine("Handled message");
    }

    public void handleClientTextMessage(int client_index, String message_text)
    {
        if (clients[client_index].messagesThrottled)
        {
            messageFloodIncrement(client_index);
            return;
        }

        messageFloodIncrement(client_index);

        StringBuilder sb = new StringBuilder();

        if (message_text.Length > 0 && message_text.First() == '!')
        {
            string message_lower = message_text.ToLower();

            if (message_lower == "!list")
            {
                //Compile list of usernames
                sb.Append("Connected users:\n");
                for (int i = 0; i < clients.Length; i++)
                {
                    if (clientIsReady(i))
                    {
                        sb.Append(clients[i].username);
                        sb.Append('\n');
                    }
                }

                sendTextMessage(client_index, sb.ToString());
                return;
            }
            else if (message_lower == "!quit")
            {
                disconnectClient(client_index, "Requested quit");
                return;
            }
            else if (message_lower.Length > (KLFCommon.GET_CRAFT_COMMAND.Length + 1)
                     && message_lower.Substring(0, KLFCommon.GET_CRAFT_COMMAND.Length) == KLFCommon.GET_CRAFT_COMMAND)
            {
                String player_name = message_lower.Substring(KLFCommon.GET_CRAFT_COMMAND.Length + 1);

                //Find the player with the given name
                int target_index = getClientIndexByName(player_name);

                if (clientIsReady(target_index))
                {
                    //Send the client the craft data
                    lock (clients[target_index].sharedCraftLock)
                    {
                        if (clients[target_index].sharedCraftName.Length > 0
                                && clients[target_index].sharedCraftFile != null && clients[target_index].sharedCraftFile.Length > 0)
                        {
                            sendCraftFile(client_index,
                                          clients[target_index].sharedCraftName,
                                          clients[target_index].sharedCraftFile,
                                          clients[target_index].sharedCraftType);

                            stampedConsoleWriteLine("Sent craft " + clients[target_index].sharedCraftName
                                                    + " to client " + clients[client_index].username);
                        }
                    }
                }

                return;
            }
        }

        //Compile full message
        sb.Append('[');
        sb.Append(clients[client_index].username);
        sb.Append("] ");
        sb.Append(message_text);

        String full_message = sb.ToString();

        //Console.SetCursorPosition(0, Console.CursorTop);
        stampedConsoleWriteLine(full_message);

        //Send the update to all other clients
        sendTextMessageToAll(full_message, client_index);
    }

    public static byte[] buildMessageArray(KLFCommon.ServerMessageID id, byte[] data)
    {
        //Construct the byte array for the message
        int msg_data_length = 0;
        if (data != null)
            msg_data_length = data.Length;

        byte[] message_bytes = new byte[KLFCommon.MSG_HEADER_LENGTH + msg_data_length];

        KLFCommon.intToBytes((int)id).CopyTo(message_bytes, 0);
        KLFCommon.intToBytes(msg_data_length).CopyTo(message_bytes, 4);
        if (data != null)
            data.CopyTo(message_bytes, KLFCommon.MSG_HEADER_LENGTH);

        return message_bytes;
    }

    private void sendMessageHeaderDirect(TcpClient client, KLFCommon.ServerMessageID id, int msg_length)
    {
        client.GetStream().Write(KLFCommon.intToBytes((int)id), 0, 4);
        client.GetStream().Write(KLFCommon.intToBytes(msg_length), 0, 4);

        debugConsoleWriteLine("Sending message: " + id.ToString());
    }

    private void sendHandshakeRefusalMessageDirect(TcpClient client, String message)
    {
        try
        {

            //Encode message
            UnicodeEncoding encoder = new UnicodeEncoding();
            byte[] message_bytes = encoder.GetBytes(message);

            sendMessageHeaderDirect(client, KLFCommon.ServerMessageID.HANDSHAKE_REFUSAL, message_bytes.Length);

            client.GetStream().Write(message_bytes, 0, message_bytes.Length);

            client.GetStream().Flush();

        }
        catch (System.IO.IOException)
        {
        }
        catch (System.ObjectDisposedException)
        {
        }
        catch (System.InvalidOperationException)
        {
        }
    }

    private void sendConnectionEndMessageDirect(TcpClient client, String message)
    {
        try
        {

            //Encode message
            UnicodeEncoding encoder = new UnicodeEncoding();
            byte[] message_bytes = encoder.GetBytes(message);

            sendMessageHeaderDirect(client, KLFCommon.ServerMessageID.CONNECTION_END, message_bytes.Length);

            client.GetStream().Write(message_bytes, 0, message_bytes.Length);

            client.GetStream().Flush();

        }
        catch (System.IO.IOException)
        {
        }
        catch (System.ObjectDisposedException)
        {
        }
        catch (System.InvalidOperationException)
        {
        }
    }

    private void sendHandshakeMessage(int client_index)
    {
        //Encode version string
        UnicodeEncoding encoder = new UnicodeEncoding();
        byte[] version_bytes = encoder.GetBytes(KLFCommon.PROGRAM_VERSION);

        byte[] data_bytes = new byte[version_bytes.Length + 12];

        //Write net protocol version
        KLFCommon.intToBytes(KLFCommon.NET_PROTOCOL_VERSION).CopyTo(data_bytes, 0);

        //Write version string length
        KLFCommon.intToBytes(version_bytes.Length).CopyTo(data_bytes, 4);

        //Write version string
        version_bytes.CopyTo(data_bytes, 8);

        //Write client ID
        KLFCommon.intToBytes(client_index).CopyTo(data_bytes, 8 + version_bytes.Length);

        clients[client_index].queueOutgoingMessage(KLFCommon.ServerMessageID.HANDSHAKE, data_bytes);
    }

    private void sendServerMessageToAll(String message, int exclude_index = -1)
    {
        UnicodeEncoding encoder = new UnicodeEncoding();
        byte[] message_bytes = buildMessageArray(KLFCommon.ServerMessageID.SERVER_MESSAGE, encoder.GetBytes(message));

        for (int i = 0; i < clients.Length; i++)
        {
            if ((i != exclude_index) && clientIsReady(i))
                clients[i].queueOutgoingMessage(message_bytes);
        }
    }

    private void sendServerMessage(int client_index, String message)
    {
        UnicodeEncoding encoder = new UnicodeEncoding();
        clients[client_index].queueOutgoingMessage(KLFCommon.ServerMessageID.SERVER_MESSAGE, encoder.GetBytes(message));
    }
    public static string RemoveSpecialCharacters(string str)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < str.Length; i++)
        {
            if ((str[i] >= '0' && str[i] <= '9')
                || (str[i] >= 'A' && str[i] <= 'z')
                    || (str[i] == '.' || str[i] == '_' || 
                    str[i] == ' ' || str[i] == '!' || 
                    str[i] == ')' || str[i] == '(' ||
                    str[i] == ':' || str[i] == '?' ||
                    str[i] == ',' || str[i] == '\''||
                    str[i] == '/'))
            {
                sb.Append(str[i]);
            }
        }

        return sb.ToString();
    }
    private void sendTextMessageToAll(String message, int exclude_index = -1)
    {
        // test back
        message = RemoveSpecialCharacters(message);
        int MLTest = message.Length;
        if (MLTest > 270)
        {
        banIP(getClientIP(exclude_index));
        saveBanList();
        disconnectClient(exclude_index, "Banned from the server. Message lenght is too long");
        message = " <-- BANNED FOR HACKED MESSAGE LENGTH";
        }
        UnicodeEncoding encoder = new UnicodeEncoding();
        byte[] message_bytes = buildMessageArray(KLFCommon.ServerMessageID.TEXT_MESSAGE, encoder.GetBytes(message));

        for (int i = 0; i < clients.Length; i++)
        {
            if ((i != exclude_index) && clientIsReady(i))
                clients[i].queueOutgoingMessage(message_bytes);
        }
    }

    private void sendTextMessage(int client_index, String message)
    {
        UnicodeEncoding encoder = new UnicodeEncoding();
        clients[client_index].queueOutgoingMessage(KLFCommon.ServerMessageID.SERVER_MESSAGE, encoder.GetBytes(message));
    }

    private void sendPluginUpdateToAll(byte[] data, bool in_flight_only, int exclude_index = -1)
    {
        //Build the message array
        byte[] message_bytes = buildMessageArray(KLFCommon.ServerMessageID.PLUGIN_UPDATE, data);

        //Send the update to all other clients
        for (int i = 0; i < clients.Length; i++)
        {
            //Make sure the client is valid and in-game
            if ((i != exclude_index)
                    && clientIsReady(i)
                    && clients[i].activityLevel != ServerClient.ActivityLevel.INACTIVE
                    && (clients[i].activityLevel == ServerClient.ActivityLevel.IN_FLIGHT || !in_flight_only))
                clients[i].queueOutgoingMessage(message_bytes);
        }
    }

    private void sendScreenshot(int client_index, Screenshot screenshot)
    {
        clients[client_index].queueOutgoingMessage(KLFCommon.ServerMessageID.SCREENSHOT_SHARE, screenshot.toByteArray());
    }

    private void sendScreenshotToWatchers(int client_index, Screenshot screenshot)
    {
        //Create a list of valid watchers
        List<int> watcher_indices = new List<int>();

        for (int i = 0; i < clients.Length; i++)
        {
            if (clientIsReady(i) && clients[i].activityLevel != ServerClient.ActivityLevel.INACTIVE)
            {
                bool match = false;

                lock (clients[i].watchPlayerNameLock)
                {
                    match = clients[i].watchPlayerName == clients[client_index].username;
                }

                if (match)
                    watcher_indices.Add(i);
            }
        }

        if (watcher_indices.Count > 0)
        {
            //Build the message and send it to all watchers
            byte[] message_bytes = buildMessageArray(KLFCommon.ServerMessageID.SCREENSHOT_SHARE, screenshot.toByteArray());
            foreach (int i in watcher_indices)
            {
                clients[i].queueOutgoingMessage(message_bytes);
            }
        }
    }

    private void sendCraftFile(int client_index, String craft_name, byte[] data, byte type)
    {

        UnicodeEncoding encoder = new UnicodeEncoding();
        byte[] name_bytes = encoder.GetBytes(craft_name);

        byte[] bytes = new byte[5 + name_bytes.Length + data.Length];

        //Copy data
        bytes[0] = type;
        KLFCommon.intToBytes(name_bytes.Length).CopyTo(bytes, 1);
        name_bytes.CopyTo(bytes, 5);
        data.CopyTo(bytes, 5 + name_bytes.Length);

        clients[client_index].queueOutgoingMessage(KLFCommon.ServerMessageID.CRAFT_FILE, bytes);
    }

    private void sendServerSettingsToAll()
    {
        //Build the message array
        byte[] setting_bytes = serverSettingBytes();
        byte[] message_bytes = buildMessageArray(KLFCommon.ServerMessageID.SERVER_SETTINGS, setting_bytes);

        //Send to clients
        for (int i = 0; i < clients.Length; i++)
        {
            if (clientIsValid(i))
                clients[i].queueOutgoingMessage(message_bytes);
        }
    }

    private void sendServerSettings(int client_index)
    {
        clients[client_index].queueOutgoingMessage(KLFCommon.ServerMessageID.SERVER_SETTINGS, serverSettingBytes());
    }

    private byte[] serverSettingBytes()
    {

        byte[] bytes = new byte[KLFCommon.SERVER_SETTINGS_LENGTH];

        KLFCommon.intToBytes(updateInterval).CopyTo(bytes, 0); //Update interval
        KLFCommon.intToBytes(settings.screenshotInterval).CopyTo(bytes, 4); //Screenshot interval
        KLFCommon.intToBytes(settings.screenshotSettings.maxHeight).CopyTo(bytes, 8); //Screenshot height
        bytes[12] = inactiveShipsPerClient; //Inactive ships per client

        return bytes;
    }

    //Flood limit

    void messageFloodIncrement(int index)
    {
        bool was_throttled = clients[index].messagesThrottled;
        clients[index].messageFloodIncrement();

        if (clientIsValid(index) && !was_throttled && clients[index].messagesThrottled)
        {
            long throttle_secs = settings.messageFloodThrottleTime / 1000;
            sendServerMessage(index, "You have been restricted from sending messages for " + throttle_secs + " seconds.");
            stampedConsoleWriteLine(clients[index].username + " has been restricted from sending messages for " + throttle_secs + " seconds.");
        }
    }

}
}
