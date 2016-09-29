using System;
using System.Threading;
using System.Net;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UDP_Networking {
    class Program {
        static void Main(string[] args) {
            if (args.Length == 0) {
                var server1_endpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 26400);
                var server1 = new Server(server1_endpoint);
                server1.Start();

                var server2_endpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 26500);
                //var server2 = new Server(server2_endpoint);
                //server2.Start();

                var send = new Thread(() => {
                    for (int i = 0; i < 200; ++i) {
                        server1.SendMessage(i + " Hello from Server 1!", server2_endpoint, true, 1);
                        Thread.Sleep(100);
                    }
                });
                send.Start();

                /*var listen = new Thread(() => {
                    var msg_received = 0;
                    while (true) {
                        while (server2.HasMessage()) {
                            var message = server2.GetNextMessage();
                            var s = Encoding.ASCII.GetString(message);
                            //Console.WriteLine(string.Format("Message: '{0}'", s));
                            msg_received++;
                        }
                    }
                });
                listen.Start();

                Thread.Sleep(10000);
                server2.Close();

                //Thread.Sleep(3000);
                //server1.Start();
                //server2.Close();

                //while (true) ; */
            }
            else {
                var server_endpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), int.Parse(args[0]));
                var server = new Server(server_endpoint);
                server.Start();

                var to_endpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), int.Parse(args[1]));
                server.SendMessage(args[2], to_endpoint);
            }
        }
    }
}
