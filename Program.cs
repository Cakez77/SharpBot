using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

internal class Program
{
  private static HttpClient Client = new HttpClient();
  private static ClientWebSocket WebSocket = new ClientWebSocket();

  private static void Main(string[] args)
  {
    /// IMPORTANT The EventSub WebSocket server supports only outgoing messages. 
    /// If you send a message to the server, except for Pong messages, the server closes the connection.

    WebSocketReceiveResult receiveResult;
    byte[] buffer = new byte[8192];
    string sessionID = null;
    string message;
    int temp, temp2;

    GetNewAccessToken();
    GetChannelID();

    // Create WebSocket connection
    // We need to set client ID and auth before connecting
    WebSocket.Options.SetRequestHeader("Client-Id", Config.BotID);
    WebSocket.Options.SetRequestHeader("Authorization", "Bearer " + Config.BotAccessToken);
    WebSocket.ConnectAsync(new Uri("wss://eventsub.wss.twitch.tv/ws"), CancellationToken.None).Wait();
    // Check if it worked
    receiveResult = WebSocket.ReceiveAsync(buffer, CancellationToken.None).Result;
    if (receiveResult.Count <= 0)
    {
      throw new Exception("Something went wrong.");
    }
    else
    {
      message = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
      Console.WriteLine(message);

      // Parse welcome message, for now assume that everything needed is inside the message, otherwise it would throw an error
      temp = message.IndexOf("\"payload\":{\"session\":{\"id\":\"");
      if (temp >= 0)
      {
        temp += "\"payload\":{\"session\":{\"id\":\"".Length;
        temp2 = message.IndexOf("\"", temp);
        sessionID = message.Substring(temp, temp2 - temp);
      }

      if (string.IsNullOrWhiteSpace(sessionID)) throw new Exception("Couldn't read session ID");

      // Subscribe to every event you want to
      // We have <10 sec to subscribe to an event, also another connection has to be used because we can't send messages to websocket server
      using (HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("POST"), "https://api.twitch.tv/helix/eventsub/subscriptions"))
      {
        string penis = 
        $$"""
        {
          "type": "channel.follow",
          "version": "2",
          "condition":
          { 
            "broadcaster_user_id": "{{Config.ChannelID}}",
            "moderator_user_id": "{{Config.ChannelID}}"
          },
          "transport":
          {
            "method":"websocket",
            "session_id": "{{sessionID}}"
          }
        }
        """;
        request.Headers.Add("Client-Id", Config.BotID);
        request.Headers.Add("Authorization", "Bearer " + Config.BotAccessToken);
        request.Content =  new StringContent(penis);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        string response = Client.SendAsync(request).Result.Content.ReadAsStringAsync().Result;
      }
    }

    while (true)
    {
      if (WebSocket.State == WebSocketState.Open)
      {
        receiveResult = WebSocket.ReceiveAsync(buffer, CancellationToken.None).Result;
        if (receiveResult.Count <= 0) { Console.WriteLine("Received 0 bytes or less."); }
        else
        {
          message = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
          temp = message.IndexOf("\"message_type\":\"session_keepalive\"");
          if (temp >= 0)
          {
            // Keep alive message, if it wasn't received in "keepalive_timeout_seconds" time from welcome message the connection should be restarted
            Console.WriteLine("Got keepalive message.");
          }
          else
          {
            // Some other message, print it
            Console.WriteLine(message);
          }
        }
      }
      else
      {
        // Do something? Reconnect? For now do nothing.
        Console.WriteLine("Socket not opened");
        break;
      }

      Thread.Sleep(100);
    }
  }

  static void GetChannelID()
  {
    using (HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("GET"), "https://api.twitch.tv/helix/users?login=" + Config.ChannelName))
    {
      request.Headers.Add("Client-Id", Config.BotID);
      request.Headers.Add("Authorization", "Bearer " + Config.BotAccessToken);

      string response = Client.SendAsync(request).Result.Content.ReadAsStringAsync().Result;
      // Read information from received data
      if (response.Contains("\"id\":"))
      {
        int index = response.IndexOf("\"id\":") + 6;
        Config.ChannelID = response.Substring(index, response.IndexOf("\",", index) - index);
      }
      else
      {
        throw new Exception(">> Couldn't acquire broadcaster ID. Probably defined channel name doesn't exist." + Environment.NewLine + ">> Event bot initialization failed.");
      }
    }
  }

  static void GetNewAccessToken()
  {
    string uri = "https://id.twitch.tv/oauth2/authorize?" +
                  "client_id=" + Config.BotID +
                  "&redirect_uri=http://localhost:3000" +
                  "&response_type=code" +
                  // When asking for permissions the scope of permissions has to be determined 
                  // if tried to follow to event without getting permissions for it, the follow returns an error
                  "&scope=" + ("bits:read" // View Bits information for a channel
                              + "+channel:read:redemptions" // View Channel Points custom rewards and their redemptions on a channel.
                              + "+moderator:read:followers" // Read followers, needs to be a moderator...
                              ).Replace(":", "%3A");

    // Open the link for the user to complete authorization
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo() { FileName = uri, UseShellExecute = true });

    // Local server is needed to get response to user authorizing the app (to grab the access token)
    HttpListener localServer = new HttpListener();
    localServer.Prefixes.Add("http://localhost:3000/"); // Where local server should listen for connections, maybe it should be in Config.ini? Hmm
    localServer.Start();
    HttpListenerContext context = localServer.GetContext(); // Await connection

    // For now lets just redirect to twitch to hide received code in browser url
    using (HttpListenerResponse resp = context.Response)
    {
      resp.Headers.Set("Content-Type", "text/plain");
      resp.Redirect(@"https://www.twitch.tv");
    }

    // Close local server, it's no longer needed
    localServer.Close();

    string requestUrl = context.Request.Url != null ? context.Request.Url.Query : string.Empty;
    // Parse received request url
    if (requestUrl.StartsWith("?code="))
    {
      // Next step - request user token with received authorization code
      string code = requestUrl.Substring(6, requestUrl.IndexOf('&', 6) - 6);
      using (HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("POST"), "https://id.twitch.tv/oauth2/token"))
      {
        request.Content = new StringContent("client_id=" + Config.BotID +
                                            "&client_secret=" + Config.BotSecret +
                                            "&code=" + code +
                                            "&grant_type=authorization_code" +
                                            "&redirect_uri=http://localhost:3000");
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded");

        string response = Client.SendAsync(request).Result.Content.ReadAsStringAsync().Result;
        // Read information from received data
        int temp, temp2;
        temp = response.IndexOf("\"access_token\":\"");
        if (temp >= 0)
        {
          temp += "\"access_token\":\"".Length;
          temp2 = response.IndexOf("\"", temp);
          Config.BotAccessToken = response.Substring(temp, temp2 - temp);
        }
        temp = response.IndexOf("\"refresh_token\":\"");
        if (temp >= 0)
        {
          temp += "\"refresh_token\":\"".Length;
          temp2 = response.IndexOf("\"", temp);
          Config.BotRefreshToken = response.Substring(temp, temp2 - temp);
        }
      }
    }
    else
    {
      // Something went wrong
      throw new NotImplementedException("Implement something here :)");
    }
  }
}
