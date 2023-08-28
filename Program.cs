using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

class SharpBot
{
  private static bool running = true;

  static int Main(string []args)
  {
    byte[] buffer = new byte[8192];

    // Connect to Twitch
    // socket.Connect("irc.chat.twitch.tv", 6667);
    // socket.Send(Encoding.UTF8.GetBytes("PASS oauth:klsdjfkljf9wqpfwp9fj2p9fjöl\r\n"));
    // socket.Send(Encoding.UTF8.GetBytes("NICK maxxxfunky\r\n"));
    // socket.Send(Encoding.UTF8.GetBytes("JOIN #cakez77\r\n"));
    ClientWebSocket socket = new ClientWebSocket();
    socket.ConnectAsync(new Uri("wss://eventsub.wss.twitch.tv/ws"), CancellationToken.None).Wait();
    socket.ReceiveAsync(buffer, CancellationToken.None).Wait();
    Console.WriteLine(Encoding.UTF8.GetString(buffer));

    while (running)
    {
      double x = Math.Sin(2.0);
      float y = MathF.Sin(2.0f);

      // Update
      Console.WriteLine("Hello, World!");
      Thread.Sleep(100);
      socket.ReceiveAsync(buffer, CancellationToken.None).Wait();

      // Rest of the Owel
    }

    return 0;
  }
}