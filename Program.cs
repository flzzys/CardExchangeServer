using System;
using System.Threading;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json;

namespace CardExchangeServer {
    //客户端信息
    class ClientInfo {
        public Socket socket;
        public byte[] readBuffer = new byte[1024];

        //剩余时间
        public float lifeTime;

        public ClientData data;
    }

    //位置，经纬度高度
    public class Location {
        //纬度
        public float latitude;
        //经度
        public float longitude;
        //高度
        public float altitude;

        public override string ToString() {
            return string.Format("({0}, {1})", latitude, longitude);
        }
    }

    //用户数据
    public class ClientData {
        public string msg;
        public string color;
        public Location loc;
        public DateTime time;
    }

    //服务器发回数据
    public class ServerData {
        public string client;
        public string msg;
        public string color;
        public float distance;
    }

    class Program {
        //服务器
        Socket server;
        int port = 1234;

        //用户列表
        Dictionary<Socket, ClientInfo> clientInfoDic = new Dictionary<Socket, ClientInfo>();

        static Program instance;

        const float DefaultLifetime = 8;

        //开始
        static void Main(string[] args) {
            instance = new Program();
            instance.Start();

            Console.Read();
        }

        void Start() {
            //开始广播
            //string ip = GetLocalIPv4();
            //instance.StartBroadcast(ip);

            //启动服务器
            instance.StartServer();

            //开始自动Update
            Thread update = new Thread(new ThreadStart(instance.StartUpdating));
            update.Start();
        }

        void StartUpdating() {
            while (true) {
                Update();

                Thread.Sleep(1000);
            }
        }

        private void Update() {
            //更新客户端剩余时间
            UpdateClientLifeTime();

            //接收信息
            UpdateReceiveMsg();
        }


        #region 服务器相关

        //启动服务器
        public void StartServer() {
            server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            IPAddress ip = IPAddress.Parse(GetLocalIPv4());
            IPEndPoint iep = new IPEndPoint(ip, port);
            server.Bind(iep);
            server.Listen(0);

            Console.WriteLine(string.Format("服务器启动! (IP: {0})", GetLocalIPv4()));

            //开始监听
            server.BeginAccept(AcceptCallback, server);
        }

        //接收客户端
        void AcceptCallback(IAsyncResult ar) {
            try {
                Socket server = (Socket)ar.AsyncState;
                Socket client = server.EndAccept(ar);

                Console.WriteLine(string.Format(string.Format("{0}已加入", GetIP(client))));

                OnReceiveClient(client);

                //继续监听
                server.BeginAccept(AcceptCallback, server);

            } catch (Exception e) {
                Console.WriteLine(e);
            }
        }

        //接收信息
        void UpdateReceiveMsg() {
            foreach (var info in clientInfoDic) {
                Socket client = info.Value.socket;

                if (client == null)
                    return;

                //有东西可接收
                if (client.Poll(0, SelectMode.SelectRead)) {
                    byte[] readBuffer = new byte[1024];
                    int count = client.Receive(readBuffer);

                    string ip = GetIP(client);

                    //收到信息小于等于0，代表客户端关闭
                    if (count <= 0) {
                        clientInfoDic.Remove(info.Key);
                        info.Value.socket.Close();

                        Console.WriteLine(string.Format(string.Format("{0}已离线", ip)));

                        return;
                    }

                    string receiveStr = System.Text.Encoding.Default.GetString(readBuffer, 0, count);

                    OnReceiveMsg(info.Value, receiveStr);
                }
            }
        }

        //更新客户端剩余时间
        void UpdateClientLifeTime() {
            List<Socket> itemToRemove = new List<Socket>();

            foreach (var item in clientInfoDic) {
                ClientInfo info = item.Value;
                info.lifeTime--;

                Console.WriteLine(info.lifeTime);

                //移除
                if (info.lifeTime <= 0) {
                    itemToRemove.Add(item.Key);
                }
            }

            foreach (var item in itemToRemove) {
                RemoveClient(clientInfoDic[item]);
            }
        }

        //移除客户端
        void RemoveClient(ClientInfo info) {
            Console.WriteLine(string.Format("客户端{0}超过时限已被移除", GetIP(info.socket)));

            info.socket.Close();
            clientInfoDic.Remove(info.socket);
        }

        #endregion

        #region API

        //当接收客户端
        void OnReceiveClient(Socket client) {
            //新增客户信息
            ClientInfo info = new ClientInfo() { socket = client, lifeTime = DefaultLifetime };
            clientInfoDic.Add(client, info);
        }

        //当收到消息
        void OnReceiveMsg(ClientInfo info, string msg) {
            //转换为客户端数据
            ClientData data = JsonConvert.DeserializeObject<ClientData>(msg);
            Console.WriteLine(data.time);

            //加入客户端列表
            clientInfoDic[info.socket].data = data;

            //遍历客户端列表，找出位置接近的
            Console.WriteLine("---开始遍历客户端列表---");
            foreach (var item in clientInfoDic) {
                //跳过自己
                if (item.Value == info) {
                    continue;
                }

                if (item.Value.data == null)
                    continue;

                float distance = GetDistance(data.loc, item.Value.data.loc);

                //比较位置在50米内
                if (distance < 200) {
                    Console.WriteLine(string.Format("{0} 距离{1}米", GetIP(item.Key), distance));

                    //重置剩余时间
                    item.Value.lifeTime = DefaultLifetime;

                    //互相发卡
                    ServerData data1 = new ServerData();
                    data1.client = GetIP(item.Key);
                    data1.color = item.Value.data.color;
                    data1.distance = distance;
                    data1.msg = item.Value.data.msg;
                    string s = JsonConvert.SerializeObject(data1);
                    Send(info.socket, s);

                    ServerData data2 = new ServerData();
                    data2.client = GetIP(info.socket);
                    data2.color = info.data.color;
                    data2.distance = distance;
                    data2.msg = info.data.msg;
                    string s2 = JsonConvert.SerializeObject(data2);
                    Send(item.Key, s2);
                }
            }
        }

        //发送消息
        void Send(Socket socket, string msg) {
            byte[] bytes = System.Text.Encoding.Default.GetBytes(msg);
            socket.Send(bytes);
        }

        #endregion

        #region 广播

        const int udpPort = 12345;

        Socket broadcaster;
        IPEndPoint iep;
        byte[] data;

        //开始广播IP
        public void StartBroadcast(string msg) {
            broadcaster = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            broadcaster.EnableBroadcast = true;
            iep = new IPEndPoint(IPAddress.Broadcast, udpPort);
            broadcaster.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, 1);

            data = System.Text.Encoding.ASCII.GetBytes(msg);

            Thread thread = new Thread(new ThreadStart(Brocast));
            thread.Start();
        }
        //每秒向局域网内广播一次
        void Brocast() {
            while (broadcaster != null) {
                broadcaster.SendTo(data, iep);

                Thread.Sleep(1000);
            }
        }

        //停止广播
        public void StopBroadcasting() {
            if (broadcaster != null) {
                broadcaster.Close();
                broadcaster = null;
            }
        }

        #endregion

        //--------------------------------------------------------------------------------------

        //获取IP地址
        public static string GetLocalIPv4() {
            string hostName = Dns.GetHostName();
            IPHostEntry iPEntry = Dns.GetHostEntry(hostName);
            for (int i = 0; i < iPEntry.AddressList.Length; i++) {
                //从IP地址列表中筛选出IPv4类型的IP地址
                if (iPEntry.AddressList[i].AddressFamily == AddressFamily.InterNetwork)
                    return iPEntry.AddressList[i].ToString();
            }
            return null;
        }

        //获取Socket的IP
        string GetIP(Socket socket) {
            return ((IPEndPoint)socket.RemoteEndPoint).Address.ToString();
        }

        //根据两地经纬度计算距离，单位为米
        public static float GetDistance(Location loc1, Location loc2) {
            float lat1, lon1, lat2, lon2;
            lat1 = loc1.latitude;
            lon1 = loc1.longitude;
            lat2 = loc2.latitude;
            lon2 = loc2.longitude;

            var R = 6378.137; // Radius of earth in KM
            var dLat = lat2 * Math.PI / 180 - lat1 * Math.PI / 180;
            var dLon = lon2 * Math.PI / 180 - lon1 * Math.PI / 180;
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
            Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
            Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            var d = R * c;
            return (float)d * 1000;
        }
    }
}
